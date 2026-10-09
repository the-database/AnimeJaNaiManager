using AnimeJaNaiConfEditor.Services.Diagnostics;
using System.Diagnostics;
using System.IO.Compression;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

// A controlled fake player tests graceful shutdown, a startup hang, and process
// ownership without changing the user's player settings or graphics drivers.
if (args.Any(a => a.StartsWith("--log-file=")))
{
    string log = args.First(a => a.StartsWith("--log-file="))[11..];
    File.WriteAllText(log, "fake player startup\n");
    if (Environment.GetEnvironmentVariable("AJN_TEST_HANG") == "1") await Task.Delay(Timeout.Infinite);
    string pipe = args.First(a => a.StartsWith("--input-ipc-server="))[19..].Replace(@"\\.\pipe\", "");
    while (true)
    {
        using var server = new NamedPipeServerStream(pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        await server.WaitForConnectionAsync();
        string command = (await new StreamReader(server).ReadLineAsync())!;
        if (command.Contains("quit")) break;
        Check(command.Contains("load-script"), "Unexpected diagnostic IPC command");
        File.WriteAllText(Environment.GetEnvironmentVariable("AJN_DIAGNOSTIC_TIMELINE")!, "{\"event\":\"startup\"}\n");
    }
    return;
}

string output = Path.GetFullPath(args.Length == 0 ? "diagnostics-test-output" : args[0]);
Directory.CreateDirectory(output);
if (args.Length > 1 && args[1] == "--live")
{
    // Optional integration check against an explicitly supplied isolated install.
    var live = new SupportReport(args[2], Path.Combine(args[2], "animejanai"), output,
        "Live diagnostic recorder validation", args.Length > 3 ? args[3] : null);
    await SupportCapture.RecordAsync(live, new Progress<string>(Console.WriteLine), CancellationToken.None, TimeSpan.FromSeconds(12));
    string zip = await live.SaveAsync(output);
    Console.WriteLine("LIVE REPORT: " + zip);
    return;
}

string fixture = Path.Combine(output, "fixture-" + Guid.NewGuid().ToString("N"));
string root = Path.Combine(fixture, "install with spaces");
string data = Path.Combine(root, "animejanai");
string config = Path.Combine(root, "portable_config");
foreach (string directory in new[] { data, config, Path.Combine(config, "watch_later"), Path.Combine(data, "onnx"), Path.Combine(data, "backups") }) Directory.CreateDirectory(directory);
File.WriteAllText(Path.Combine(data, "animejanai.conf"), "[global]\ntrt_engine_settings=--skipInference\npassword=PRIVATE_PASSWORD\n");
File.WriteAllText(Path.Combine(config, "mpv.conf"), "video-sync=display-resample\nhttp-header-fields=Authorization: Bearer PRIVATE_HEADER\n");
File.WriteAllText(Path.Combine(config, "watch_later", "private.conf"), "PRIVATE_HISTORY");
File.WriteAllText(Path.Combine(data, "onnx", "model.onnx"), "PRIVATE_MODEL_BYTES");
File.WriteAllText(Path.Combine(data, "onnx", "model.engine"), "PRIVATE_ENGINE_BYTES");
File.WriteAllText(Path.Combine(data, "onnx", "model.engine.build.log"), "engine rejects input 480x360\nhttps://example.test/video?X-Plex-Token=PRIVATE_QUERY\n");
File.WriteAllText(Path.Combine(data, "currentanimejanai.log"), "FIRST_LOG_LINE\n" + new string('x', SupportReport.FileLimit + 1000) + "\nLAST_LOG_LINE");
var report = new SupportReport(root, data, fixture, @"User notes C:\Users\Someone\Videos\episode.mkv");
report.CaptureStartingSettings();
File.AppendAllText(Path.Combine(data, "animejanai.conf"), "backend=TensorRT\n");
File.WriteAllText(Path.Combine(report.Work, "timeline.jsonl"), JsonSerializer.Serialize(new {
    time_pos = 42, media = Path.Combine(root, "video.mkv"), token = "PRIVATE_JSON", url = "https://user:PRIVATE_URL@example.test/play?auth=PRIVATE_URL_QUERY" }) + "\n");
report.Mark("Problem now");
string destination = await report.SaveAsync(output);
using (var zip = ZipFile.OpenRead(destination))
{
    string Read(string name) { using var r = new StreamReader(zip.GetEntry(name)!.Open()); return r.ReadToEnd(); }
    string all = string.Join('\n', zip.Entries.Select(e => { using var reader = new StreamReader(e.Open()); return reader.ReadToEnd(); }));
    foreach (string secret in new[] { "PRIVATE_PASSWORD", "PRIVATE_HEADER", "PRIVATE_QUERY", "PRIVATE_JSON", "PRIVATE_URL", "PRIVATE_HISTORY", "PRIVATE_MODEL_BYTES", "PRIVATE_ENGINE_BYTES" })
        Check(!all.Contains(secret), "Export leaked " + secret);
    Check(!zip.Entries.Any(e => e.FullName.EndsWith(".onnx") || e.FullName.EndsWith(".engine")), "Binary asset included");
    Check(Read("logs/currentanimejanai.log").Contains("FIRST_LOG_LINE") && Read("logs/currentanimejanai.log").Contains("LAST_LOG_LINE"), "Log endpoints lost");
    Check(Read("settings/portable_config/mpv.conf").Contains("video-sync=display-resample"), "Useful setting was redacted");
    Check(!Read("settings-at-start/animejanai.conf").Contains("backend=TensorRT"), "Starting config was overwritten");
    Check(Read("settings/animejanai.conf").Contains("backend=TensorRT"), "Final config missing");
    Check(JsonNode.Parse(Read("report.json"))!["warnings"]!.AsArray().Count > 0, "Truncation not disclosed");
    _ = JsonNode.Parse(Read("recording/timeline.jsonl"));
}
Console.WriteLine("PASS bundle contents, redaction, valid JSON, log limits, initial/final config snapshots");

// Use the test apphost as mpvnet.exe. Its dependencies stay beside it.
foreach (string file in Directory.GetFiles(AppContext.BaseDirectory)) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
File.Copy(Environment.ProcessPath!, Path.Combine(root, "mpvnet.exe"));
var info = SupportCapture.LaunchInfo(report, "test-pipe");
Check(info.ArgumentList.Contains("--process-instance=multi"), "Single-instance forwarding was not disabled");
Check(!info.ArgumentList.Any(a => a.Contains("no-config") || a.Contains("gpu-api=")), "Recorder changed reproduction settings");
Console.WriteLine("PASS independent player command preserves renderer/config settings");

foreach (bool hang in new[] { false, true })
{
    Environment.SetEnvironmentVariable("AJN_TEST_HANG", hang ? "1" : null);
    using var unrelated = Process.Start(new ProcessStartInfo(Path.Combine(root, "mpvnet.exe")) {
        UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--log-file=" + Path.Combine(fixture, "other.log"), "--input-ipc-server=" + Guid.NewGuid().ToString("N") }
    })!;
    try
    {
        var recording = new SupportReport(root, data, fixture, "shutdown test");
        await SupportCapture.RecordAsync(recording, new Progress<string>(), CancellationToken.None, TimeSpan.FromSeconds(1));
        var result = JsonNode.Parse(File.ReadAllText(Path.Combine(recording.Work, "result.json")))!;
        Check(result["exited"]!.GetValue<bool>(), "Diagnostic player was left running");
        Check(!unrelated.HasExited, "Recorder stopped an unrelated player");
        Check(hang == (recording.Warnings.Count > 0), "Unexpected graceful/forced shutdown result");
        Check(File.Exists(Path.Combine(recording.Work, "modules.json")), "No loaded-module evidence");
    }
    finally { if (!unrelated.HasExited) unrelated.Kill(entireProcessTree: true); await unrelated.WaitForExitAsync(); }
    Console.WriteLine("PASS " + (hang ? "hung startup saved and owned process stopped" : "normal player closed through private IPC"));
}
Environment.SetEnvironmentVariable("AJN_TEST_HANG", null);
Console.WriteLine("ALL DIAGNOSTICS CHECKS PASSED");
