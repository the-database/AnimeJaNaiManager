using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services.Diagnostics;

public static class SupportCapture
{
    public static ProcessStartInfo LaunchInfo(SupportReport report, string pipe)
    {
        string player = Path.Combine(report.Root, "mpvnet.exe");
        bool mpvnet = File.Exists(player);
        if (!mpvnet) player = Path.Combine(report.Root, OperatingSystem.IsWindows() ? "mpv.exe" : "mpv");
        if (!File.Exists(player)) throw new FileNotFoundException("The player was not found beside this Manager. Run Manager from your AnimeJaNai installation.", player);
        var info = new ProcessStartInfo(player) { WorkingDirectory = report.Root, UseShellExecute = false,
            RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        // mpv.net is normally single-instance. The test must own its process so a
        // hung startup can be stopped without touching an already-running player.
        if (mpvnet) info.ArgumentList.Add("--process-instance=multi");
        info.ArgumentList.Add("--log-file=" + Path.Combine(report.Work, "player.log"));
        info.ArgumentList.Add("--msg-level=all=v");
        info.ArgumentList.Add("--input-ipc-server=" + (OperatingSystem.IsWindows() ? @"\\.\pipe\" + pipe : pipe));
        info.Environment["AJN_DIAGNOSTIC_TIMELINE"] = Path.Combine(report.Work, "timeline.jsonl");
        info.Environment["VK_LOADER_DEBUG"] = "error,warn,layer";
        if (report.Media != null) info.ArgumentList.Add(Path.GetFullPath(report.Media));
        return info;
    }

    public static async Task RecordAsync(SupportReport report, IProgress<string> progress, CancellationToken finish,
        TimeSpan? duration = null)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Live diagnostic recording currently requires Windows. Save a diagnostics snapshot instead.");
        await Task.Run(report.CaptureStartingSettings);
        string script = Path.Combine(report.Work, "capture.lua");
        using (var resource = typeof(SupportCapture).Assembly.GetManifestResourceStream("AnimeJaNaiManager.Diagnostics.capture.lua")
            ?? throw new InvalidOperationException("The diagnostic player script is missing."))
        using (var file = File.Create(script)) await resource.CopyToAsync(file);
        string pipe = "AJN-diagnostics-" + report.Id;
        using var process = new Process { StartInfo = LaunchInfo(report, pipe) };
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(finish);
        stop.CancelAfter(duration ?? TimeSpan.FromMinutes(5));
        Task stdout = Task.CompletedTask, stderr = Task.CompletedTask;
        Task probe = Task.CompletedTask;
        string reason = "Player closed";
        bool launched = false;
        var modules = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        try
        {
            process.Start(); launched = true;
            stdout = DrainAsync(process.StandardOutput, Path.Combine(report.Work, "stdout.log"));
            stderr = DrainAsync(process.StandardError, Path.Combine(report.Work, "vulkan-loader.log"));
            // mpv.net applies list-append arguments after libmpv initializes,
            // so --scripts-append does not execute the added script. Load it
            // through our private IPC endpoint without replacing user scripts.
            probe = LoadProbeAsync(pipe, script, stop.Token);
            var timer = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                Snapshot(process, report, modules);
                progress.Report(timer.Elapsed.TotalSeconds < 20
                    ? "Recording. Reproduce the problem in the new player, then choose Finish and save report."
                    : "Recording—even if no player window appeared. Mark the problem or choose Finish and save report.");
                if (new FileInfo(Path.Combine(report.Work, "player.log")) is { Exists: true, Length: > 32 * 1024 * 1024 })
                { reason = "Log size limit reached"; break; }
                await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        { reason = finish.IsCancellationRequested ? "User finished recording" : "Recording time limit reached"; }
        finally
        {
            stop.Cancel();
            await probe;
            if (launched)
            {
                if (!process.HasExited)
                {
                    Snapshot(process, report, modules);
                    progress.Report("Closing the diagnostic player and saving its logs…");
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous);
                        await client.ConnectAsync(timeout.Token);
                        await client.WriteAsync("{\"command\":[\"quit\"]}\n"u8.ToArray(), timeout.Token);
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
                    { report.Warnings.Add("The diagnostic player did not close normally: " + ex.Message); }
                    if (!process.HasExited)
                    {
                        try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException)
                        { report.Warnings.Add("Could not stop diagnostic player PID " + process.Id + ": " + ex.Message); }
                    }
                }
                try { await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(2)); }
                catch (Exception ex) when (ex is TimeoutException or IOException)
                { report.Warnings.Add("Player output could not be fully collected: " + ex.Message); }
                if (!File.Exists(Path.Combine(report.Work, "timeline.jsonl")))
                    report.Warnings.Add("Playback timeline unavailable: the player did not reach the diagnostic script. Startup/process logs are still included.");
                File.WriteAllText(Path.Combine(report.Work, "result.json"), JsonSerializer.Serialize(new {
                    reason, pid = process.Id, exited = process.HasExited, exit_code = process.HasExited ? (int?)process.ExitCode : null,
                    finished_utc = DateTime.UtcNow, overrides = new[] { "separate player instance", "verbose log", "diagnostic script", "private IPC pipe", "Vulkan loader logging" }
                }));
            }
        }
    }

    private static async Task LoadProbeAsync(string pipe, string script, CancellationToken stop)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out, PipeOptions.Asynchronous);
            await client.ConnectAsync(timeout.Token);
            byte[] command = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command = new[] { "load-script", script } }) + "\n");
            await client.WriteAsync(command, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException)
        { /* Missing timeline is reported at the end, including an early startup hang. */ }
    }

    // Even if libmpv hangs before scripts/IPC initialize, Windows can report
    // memory/CPU, window creation and injected graphics DLLs from this process.
    private static void Snapshot(Process process, SupportReport report, Dictionary<string, object> modules)
    {
        try
        {
            process.Refresh();
            if (process.HasExited) return;
            string? moduleError = null;
            try
            {
                foreach (ProcessModule module in process.Modules)
                    modules.TryAdd(module.FileName, new { name = module.ModuleName, path = module.FileName, first_seen_utc = DateTime.UtcNow });
                File.WriteAllText(Path.Combine(report.Work, "modules.json"), JsonSerializer.Serialize(modules.Values));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { moduleError = ex.Message; }
            File.AppendAllText(Path.Combine(report.Work, "process.jsonl"), JsonSerializer.Serialize(new {
                utc = DateTime.UtcNow, pid = process.Id, window = process.MainWindowHandle.ToInt64(),
                working_set_bytes = process.WorkingSet64, cpu_seconds = process.TotalProcessorTime.TotalSeconds,
                threads = process.Threads.Count, loaded_module_count = modules.Count, module_error = moduleError
            }) + "\n");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        { report.Warnings.Add("Process snapshot unavailable: " + ex.Message); }
    }

    private static async Task DrainAsync(StreamReader input, string path)
    {
        // Always drain the pipe so a verbose child cannot deadlock. Rotate the
        // captured output; retain both files in the ZIP, with a visible note.
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        byte[] bytes;
        char[] buffer = new char[4096];
        int count;
        while ((count = await input.ReadAsync(buffer)) != 0)
        {
            bytes = System.Text.Encoding.UTF8.GetBytes(buffer, 0, count);
            if (output.Length + bytes.Length > SupportReport.FileLimit)
            {
                await output.FlushAsync();
                File.Copy(path, path + ".previous.log", overwrite: true);
                output.SetLength(0); output.Position = 0;
                await output.WriteAsync("[AJN: output rotated; previous segment is in the adjacent previous.log]\n"u8.ToArray());
            }
            await output.WriteAsync(bytes); await output.FlushAsync();
        }
    }
}
