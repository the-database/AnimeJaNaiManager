using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services
{
    // Where the shared pack store is (the TensorRT runtime and its GPU builder packs, which every JaNai
    // app shares), through the aji-runtime at the install root: `location --json`, `move`,
    // `move --default`, `reset` (the-database/aji-runtime 0.2.0). A moved store leaves a link at the
    // default path, so the player and the other apps keep working without knowing.
    public static class StoreLocation
    {
        public sealed record Info(string Root, bool Moved, string? Target, bool Available)
        {
            // The folder the store's files are in.
            public string Folder => Moved && Target is not null ? Target : Root;

            // Moved, and the folder is there: Move Back to Default.
            public bool MovedHere => Moved && Available;

            // Moved, and the folder is gone (an unplugged drive): Try Again / Reset to Default.
            public bool Missing => Moved && !Available;

            public string Text => !Moved
                ? "The TensorRT runtime is in the default folder. All your JaNai apps share it."
                : Available
                    ? $"Moved from {Root}, which now links here. All your JaNai apps share it."
                    : "This folder isn't available. Is its drive connected? Until it is, TensorRT can't run.";
        }

        // Tests point these at an aji-runtime build and a fake install.
        public static string? AjiRuntimeOverride { get; set; }

        public static string AjiRuntimePath(string rootDir) =>
            AjiRuntimeOverride ?? Path.Combine(rootDir, OperatingSystem.IsWindows() ? "aji-runtime.exe" : "aji-runtime");

        // Whether this install's player uses the store: aji-runtime at the install root, and the
        // aji-runtime.json it writes beside libaji (only while the install has TensorRT packs in the
        // store). Not a dev build, a portable Manager, an install from before the store, a PC without
        // TensorRT, or the AppImage (its aji-runtime stays inside the image).
        public static bool UsedBy(string rootDir, string dataDir) =>
            File.Exists(AjiRuntimePath(rootDir)) && File.Exists(Path.Combine(dataDir, "inference", "aji-runtime.json"));

        // The message wherever something needs the store while its folder is missing.
        public static string MissingMessage(string folder) =>
            $"The shared runtime's folder {folder} isn't available. Is its drive connected? Reconnect it, or reset the location in Components > Storage.";

        public static Info? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                string? Str(string key) => o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                bool Bool(string key, bool fallback) => o.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
                return new Info(Str("root") ?? "", Bool("moved", false), Str("target"), Bool("available", true));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        // null when aji-runtime can't say (missing, or older than 0.2.0).
        public static async Task<Info?> GetAsync(string rootDir)
        {
            if (!File.Exists(AjiRuntimePath(rootDir)))
            {
                return null;
            }
            try
            {
                var (rc, stdout, _) = await RunAsync(rootDir, ["location", "--json"], null);
                return rc == 0 ? Parse(stdout) : null;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        // Moving into folder: aji-runtime needs a new or empty folder, so a folder with files in it
        // gets one of its own inside.
        public static string MoveTarget(string folder)
        {
            var empty = !Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any();
            return empty ? folder : Path.Combine(folder, "JaNai shared runtime");
        }

        // The bytes in the store's folder, for the confirmation (0 when it can't be read).
        public static long Size(string folder)
        {
            try
            {
                return new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return 0;
            }
        }

        // `move <target>`, `move --default` or `reset`, with progress: (copied, total) bytes from
        // AJI_MOVE lines. Returns null on success, else aji-runtime's error.
        public static async Task<string?> ChangeAsync(string rootDir, string[] args, Action<long, long>? progress)
        {
            var (rc, _, stderr) = await RunAsync(rootDir, [.. args, "--progress", "lines"], line =>
            {
                var parts = line.Split(' ');
                if (parts.Length == 3 && parts[0] == "AJI_MOVE" && long.TryParse(parts[1], out var done) && long.TryParse(parts[2], out var total))
                {
                    progress?.Invoke(done, total);
                }
            });
            if (rc == 0)
            {
                return null;
            }
            var error = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("error: ", StringComparison.Ordinal));
            return error?["error: ".Length..] ?? $"aji-runtime exited with {rc}";
        }

        private static async Task<(int, string, string)> RunAsync(string rootDir, string[] args, Action<string>? onLine)
        {
            var psi = new ProcessStartInfo(AjiRuntimePath(rootDir))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args)
            {
                psi.ArgumentList.Add(a);
            }
            using var p = new Process { StartInfo = psi };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            p.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }
                lock (stdout)
                {
                    stdout.AppendLine(e.Data);
                }
                onLine?.Invoke(e.Data);
            };
            p.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    lock (stderr)
                    {
                        stderr.AppendLine(e.Data);
                    }
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            await p.WaitForExitAsync();
            return (p.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}
