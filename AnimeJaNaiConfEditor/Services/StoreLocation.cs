using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services
{
    // Where this copy of the player keeps the TensorRT runtime and its GPU builder packs (its pack
    // store), and moving it.
    //
    // The choice (AnimeJaNaiUpdater 3.12+, `--storage --json` / `--storage <inside|shared|folder>`):
    // inside the install (portable), the shared per-user store every JaNai app can use, or a folder
    // the user picks (apps set to the same folder share it). The updater moves this copy's packs when
    // the choice changes.
    //
    // The shared store itself moves through the aji-runtime at the install root: `location --json`,
    // `move`, `move --default`, `reset` (the-database/aji-runtime 0.2.0). A moved shared store leaves
    // a link at the default path, so the player and the other apps keep working without knowing.
    // Without the choice (an older updater) that is all there is.
    public static class StoreLocation
    {
        public enum StoreChoice { Inside, Shared, Custom }

        // Root: the store folder this copy uses. Moved/Target: the shared store was moved and links
        // to Target. Available: false while the folder's drive isn't connected.
        public sealed record Info(string Root, bool Moved, string? Target, bool Available)
        {
            // null: the updater can't say (one from before the choice); the store is the shared one.
            public StoreChoice? Choice { get; init; }

            // The folders "inside" and "shared" mean for this copy (only with Choice).
            public string? Inside { get; init; }
            public string? Shared { get; init; }

            // This copy has TensorRT in its store (only with Choice).
            public bool Installed { get; init; }

            public bool IsShared => Choice is null or StoreChoice.Shared;

            // The folder the store's files are in.
            public string Folder => IsShared && Moved && Target is not null ? Target : Root;

            // Where the shared store's files are, whichever store this copy uses.
            public string SharedFolder => Moved && Target is not null ? Target : Shared ?? (IsShared ? Root : "");

            // The shared store was moved, and the folder is there: Move Back to Default.
            public bool MovedHere => IsShared && Moved && Available;

            // The folder is gone (an unplugged drive): Try Again, Reset to Default or another folder.
            public bool Missing => !Available;

            public string Text =>
                Missing ? "This folder isn't available. Is its drive connected? Until it is, TensorRT can't run."
                : Choice is null ? (Moved
                    ? $"Moved from {Root}, which now links here. All your JaNai apps share it."
                    : "The TensorRT runtime is in the default folder. All your JaNai apps share it.")
                : MovedHere ? $"Moved from {Shared ?? Root}, which now links here."
                : !Installed ? "TensorRT isn't installed yet. It will go in the folder selected above."
                : "";
        }

        // Tests point these at an aji-runtime build, a fake updater and a fake install.
        public static string? AjiRuntimeOverride { get; set; }
        public static string? UpdaterOverride { get; set; }

        public static string AjiRuntimePath(string rootDir) =>
            AjiRuntimeOverride ?? Path.Combine(rootDir, OperatingSystem.IsWindows() ? "aji-runtime.exe" : "aji-runtime");

        // The Linux package ships the updater without an extension.
        public static string UpdaterPath(string rootDir) =>
            UpdaterOverride ?? Path.Combine(rootDir, OperatingSystem.IsWindows() ? "AnimeJaNaiUpdater.exe" : "AnimeJaNaiUpdater");

        // Whether this install's player has TensorRT in the store: aji-runtime at the install root,
        // and the aji-runtime.json it writes beside libaji (only while the install has TensorRT packs
        // in the store). Not a dev build, a portable Manager, an install from before the store, a PC
        // without TensorRT, or the AppImage (its aji-runtime stays inside the image).
        public static bool UsedBy(string rootDir, string dataDir) =>
            File.Exists(AjiRuntimePath(rootDir)) && File.Exists(Path.Combine(dataDir, "inference", "aji-runtime.json"));

        // Whether Components > Storage is shown: the player uses a store (aji-runtime at the install
        // root, and the store could be read) and TensorRT is, or can be, installed here: it is, or
        // the PC has an NVIDIA GPU that can run it (nvidia is null until the updater has said).
        public static bool Shows(Info? storage, bool hasTrt, bool? nvidia) =>
            storage is not null && (hasTrt || nvidia != false);

        // The message wherever something needs the store while its folder is missing.
        public static string MissingMessage(string folder) =>
            $"The TensorRT runtime's folder {folder} isn't available. Is its drive connected? Reconnect it, or choose another place in Components > Storage.";

        // `aji-runtime location --json`.
        public static Info? Parse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                return new Info(Str(o, "root") ?? "", Bool(o, "moved", false), Str(o, "target"), Bool(o, "available", true));
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        // `AnimeJaNaiUpdater --storage --json`: null unless it names a choice and a folder.
        public static Info? ParseChoice(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var o = doc.RootElement;
                if (o.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }
                StoreChoice? choice = Str(o, "choice") switch
                {
                    "inside" => StoreChoice.Inside,
                    "shared" => StoreChoice.Shared,
                    "custom" => StoreChoice.Custom,
                    _ => null,
                };
                if (choice is null || Str(o, "root") is not { Length: > 0 } root)
                {
                    return null;
                }
                return new Info(root, Bool(o, "moved", false), Str(o, "target"), Bool(o, "available", true))
                {
                    Choice = choice,
                    Inside = Str(o, "inside"),
                    Shared = Str(o, "shared"),
                    Installed = Bool(o, "installed", false),
                };
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                return null;
            }
        }

        private static string? Str(JsonElement o, string key) =>
            o.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool Bool(JsonElement o, string key, bool fallback) =>
            o.TryGetProperty(key, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;

        // What Components > Storage shows: the updater's choice, else (an updater from before it)
        // where the shared store is, if the install has TensorRT in it. null: nothing to show.
        public static async Task<Info?> ReadAsync(string rootDir, string dataDir)
        {
            if (!File.Exists(AjiRuntimePath(rootDir)))
            {
                return null;
            }
            return await GetChoiceAsync(rootDir) ?? (UsedBy(rootDir, dataDir) ? await GetAsync(rootDir) : null);
        }

        // `AnimeJaNaiUpdater --storage --json`; null when the updater can't say (missing, or older).
        public static async Task<Info?> GetChoiceAsync(string rootDir)
        {
            string updater = UpdaterPath(rootDir);
            if (!File.Exists(updater))
            {
                return null;
            }
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var (rc, stdout, _) = await RunAsync(updater, rootDir, ["--storage", "--json"], null, timeout.Token);
                return rc == 0 ? ParseChoice(stdout) : null;
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or OperationCanceledException or System.ComponentModel.Win32Exception)
            {
                return null;
            }
        }

        // `aji-runtime location --json`; null when aji-runtime can't say (missing, or older than 0.2.0).
        public static async Task<Info?> GetAsync(string rootDir)
        {
            if (!File.Exists(AjiRuntimePath(rootDir)))
            {
                return null;
            }
            try
            {
                var (rc, stdout, _) = await RunAsync(AjiRuntimePath(rootDir), null, ["location", "--json"], null, default);
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

        // A folder that already is a store (another app keeps its packs there).
        public static bool IsStore(string folder) => File.Exists(Path.Combine(folder, "store.json"));

        // "A folder I choose": a store is used as it is (that is how two apps share one), as is a new
        // or empty folder; any other folder gets one of its own inside (which may be the store
        // another app made there the same way).
        public static string ChoiceTarget(string folder) =>
            IsStore(folder) ? folder : MoveTarget(folder);

        // The updater's argument for switching to folder: inside or shared when it is one of those.
        public static string ChoiceArgument(Info storage, string folder)
        {
            if (SameFolder(folder, storage.Inside))
            {
                return "inside";
            }
            if (SameFolder(folder, storage.Shared) || SameFolder(folder, storage.SharedFolder))
            {
                return "shared";
            }
            return Path.GetFullPath(folder);
        }

        public static bool SameFolder(string a, string? b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            {
                return false;
            }
            try
            {
                var ca = Path.TrimEndingDirectorySeparator(Path.GetFullPath(a));
                var cb = Path.TrimEndingDirectorySeparator(Path.GetFullPath(b));
                return string.Equals(ca, cb, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return false;
            }
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

        // (copied, total) bytes from an AJI_MOVE line.
        private static bool TryProgress(string line, out long done, out long total)
        {
            done = total = 0;
            var parts = line.Trim().Split(' ');
            return parts.Length == 3 && parts[0] == "AJI_MOVE" && long.TryParse(parts[1], out done) && long.TryParse(parts[2], out total);
        }

        // `move <target>`, `move --default` or `reset`, with progress: (copied, total) bytes from
        // AJI_MOVE lines. Returns null on success, else aji-runtime's error.
        public static async Task<string?> ChangeAsync(string rootDir, string[] args, Action<long, long>? progress)
        {
            var (rc, _, stderr) = await RunAsync(AjiRuntimePath(rootDir), null, [.. args, "--progress", "lines"], line =>
            {
                if (TryProgress(line, out var done, out var total))
                {
                    progress?.Invoke(done, total);
                }
            }, default);
            if (rc == 0)
            {
                return null;
            }
            var error = stderr.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("error: ", StringComparison.Ordinal));
            return error?["error: ".Length..] ?? $"aji-runtime exited with {rc}";
        }

        // `AnimeJaNaiUpdater --storage <inside|shared|folder>`: this copy uses that store from now
        // on, and its packs move along. Progress from AJI_MOVE lines. Returns null on success, else
        // the updater's reason (its last line of output).
        public static async Task<string?> SwitchAsync(string rootDir, string target, Action<long, long>? progress)
        {
            var lines = new List<string>();
            var (rc, _, _) = await RunAsync(UpdaterPath(rootDir), rootDir, ["--storage", target], line =>
            {
                if (TryProgress(line, out var done, out var total))
                {
                    progress?.Invoke(done, total);
                }
                else if (line.Trim() is { Length: > 0 } text)
                {
                    lock (lines)
                    {
                        lines.Add(text);
                    }
                }
            }, default, errorsToo: true);
            if (rc == 0)
            {
                return null;
            }
            lock (lines)
            {
                return lines.Count > 0 ? lines[^1] : $"{Path.GetFileName(UpdaterPath(rootDir))} exited with {rc}";
            }
        }

        // onLine sees stdout's lines (and stderr's, with errorsToo) as they come.
        private static async Task<(int, string, string)> RunAsync(string exe, string? workingDirectory, string[] args,
            Action<string>? onLine, CancellationToken cancel, bool errorsToo = false)
        {
            var psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            if (workingDirectory is not null)
            {
                psi.WorkingDirectory = workingDirectory;
            }
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
                if (e.Data is null)
                {
                    return;
                }
                lock (stderr)
                {
                    stderr.AppendLine(e.Data);
                }
                if (errorsToo)
                {
                    onLine?.Invoke(e.Data);
                }
            };
            p.Start();
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            try
            {
                await p.WaitForExitAsync(cancel);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    p.Kill(true);
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw;
            }
            return (p.ExitCode, stdout.ToString(), stderr.ToString());
        }
    }
}
