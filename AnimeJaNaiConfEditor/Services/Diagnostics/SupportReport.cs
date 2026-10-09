using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services.Diagnostics;

public sealed class SupportReport
{
    public const int FileLimit = 2 * 1024 * 1024;
    public string Root { get; }
    public string Data { get; }
    public string Work { get; }
    public string? Media { get; }
    public DateTime StartedUtc { get; } = DateTime.UtcNow;
    public string Id { get; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6];
    public List<string> Warnings { get; } = [];
    private readonly ReportRedactor _redactor;
    private readonly Dictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _notes;
    private int _markers;

    public SupportReport(string root, string data, string workParent, string notes, string? media = null)
    {
        Root = Path.GetFullPath(root); Data = Path.GetFullPath(data);
        Work = Path.Combine(Path.GetFullPath(workParent), "recording-" + Id);
        Directory.CreateDirectory(Work);
        Media = media; _notes = notes;
        _redactor = new((Root, "<install>"), (Data, "<ajn-data>"), (Work, "<recording>"),
            (media ?? "", "<selected-media>"));
        File.WriteAllText(Path.Combine(Work, "session.json"), JsonSerializer.Serialize(new { Id, StartedUtc, notes, media_selected = media != null }));
    }

    public void Mark(string note)
    {
        if (_markers++ >= 50) return;
        File.AppendAllText(Path.Combine(Work, "markers.jsonl"),
            JsonSerializer.Serialize(new { utc = DateTime.UtcNow, note = note[..Math.Min(note.Length, 4000)] }) + "\n");
    }

    // Keep the original settings even when the user changes presets during recording.
    public void CaptureStartingSettings() => CollectSettings("settings-at-start");

    private void CollectSettings(string prefix)
    {
        AddFile(prefix + "/animejanai.conf", Path.Combine(Data, "animejanai.conf"));
        string config = Path.Combine(Root, "portable_config");
        foreach (string file in Files(config, recursive: true).Where(f => Path.GetExtension(f).Equals(".conf", StringComparison.OrdinalIgnoreCase)).Take(100))
            AddFile(prefix + "/portable_config/" + Path.GetRelativePath(config, file).Replace('\\', '/'), file);
    }

    // Never traverse symlinks/junctions or private playback-history/cache directories.
    private IEnumerable<string> Files(string directory, bool recursive = false)
    {
        if (!Directory.Exists(directory)) return [];
        try
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return [];
            var files = Directory.GetFiles(directory).Where(p => (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0).ToList();
            if (recursive)
                foreach (string child in Directory.GetDirectories(directory))
                    if (Path.GetFileName(child) is not ("watch_later" or "cache" or "shader-cache")) files.AddRange(Files(child, true));
            return files;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Warnings.Add($"Could not list {directory}: {ex.Message}"); return []; }
    }

    public static string ReadBounded(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length <= FileLimit)
            return new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true).ReadToEnd();
        var first = new byte[FileLimit / 4];
        stream.ReadExactly(first);
        stream.Seek(-FileLimit * 3 / 4, SeekOrigin.End);
        string last = new StreamReader(stream, Encoding.UTF8).ReadToEnd();
        return Encoding.UTF8.GetString(first) + "\n[AJN: middle omitted; first and last log data retained]\n" + last;
    }

    private void AddFile(string name, string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { Warnings.Add($"Skipped linked file: {path}"); return; }
            string text = ReadBounded(path);
            if (new FileInfo(path).Length > FileLimit) Warnings.Add($"Truncated {name} to its beginning and end.");
            AddText(name, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Warnings.Add($"Could not read {path}: {ex.Message}"); }
    }

    private void AddText(string name, string text)
    {
        if (name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            try { var node = JsonNode.Parse(text); _redactor.Json(node); _documents[name] = node?.ToJsonString(new() { WriteIndented = true }) ?? "null"; return; }
            catch (JsonException) { Warnings.Add($"{name} was not valid JSON; exported as redacted text."); }
        }
        if (name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            text = string.Join('\n', text.Split('\n').Select(line =>
            {
                try { var node = JsonNode.Parse(line); _redactor.Json(node); return node?.ToJsonString() ?? ""; }
                catch (JsonException) { return _redactor.Text(line); }
            }));
            _documents[name] = text;
        }
        else _documents[name] = _redactor.Text(text);
    }

    private object[] Inventory(string directory, bool hashes = false) => Files(directory).Take(300).Select(path =>
    {
        var info = new FileInfo(path);
        string? version = null, hash = null;
        List<long>? dimensions = null;
        try
        {
            if (info.Extension is ".exe" or ".dll") version = FileVersionInfo.GetVersionInfo(path).FileVersion;
            if (hashes && info.Length < 128 * 1024 * 1024 && info.Extension is ".exe" or ".dll" or ".lua")
            { using var stream = File.OpenRead(path); hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
            if (info.Extension == ".onnx" && info.Length < 64 * 1024 * 1024) dimensions = OnnxInputShape.InputDims(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { Warnings.Add($"Metadata unavailable for {path}: {ex.Message}"); }
        return (object)new { name = info.Name, bytes = info.Length, modified_utc = info.LastWriteTimeUtc, version, sha256 = hash, input_dimensions = dimensions };
    }).ToArray();

    public async Task<string> SaveAsync(string outputDirectory, IProgress<string>? progress = null)
    {
        progress?.Report("Collecting GPU, driver and display details…");
        var system = await DiagnosticSystem.CollectAsync();
        AddText("system.json", JsonSerializer.Serialize(system));
        await Task.Run(() =>
        {
            progress?.Report("Collecting settings, engine logs and component versions…");
            CollectSettings("settings");
            foreach (string name in new[] { "version.txt", "components.json" }) AddFile(name, Path.Combine(Root, name));
            foreach (string name in new[] { "mpv.log", "mpvnet.log", "mpvnet-startup.log" }) AddFile("logs/" + name, Path.Combine(Root, name));
            foreach (string file in Files(Data).Where(f => Path.GetExtension(f) == ".log" || Path.GetFileName(f) is "benchmark.txt"))
                AddFile("logs/" + Path.GetFileName(file), file);
            foreach (string folder in new[] { "onnx", "rife" })
                foreach (string file in Files(Path.Combine(Data, folder)).Where(f => f.EndsWith(".build.log", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(6)) AddFile("engine-logs/" + folder + "/" + Path.GetFileName(file), file);
            foreach (string file in Files(Path.Combine(Data, "backups")).Where(f => f.EndsWith(".conf", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc).Take(3)) AddFile("recent-config-backups/" + Path.GetFileName(file), file);
            AddText("inventory.json", JsonSerializer.Serialize(new {
                root = Inventory(Root, true), inference = Inventory(Path.Combine(Data, "inference"), true),
                models = Inventory(Path.Combine(Data, "onnx")), rife = Inventory(Path.Combine(Data, "rife")),
                scripts = Inventory(Path.Combine(Root, "portable_config", "scripts"), true),
                shaders = Inventory(Path.Combine(Root, "portable_config", "shaders"), true)
            }));
            foreach (string file in Files(Work).Where(f => Path.GetExtension(f) is ".log" or ".json" or ".jsonl"))
                AddFile("recording/" + Path.GetFileName(file), file);
            // A Manager/Windows crash may interrupt export. Recover the newest
            // recording's text evidence without running anything from its folder.
            string? previous = Directory.GetDirectories(Path.GetDirectoryName(Work)!, "recording-*")
                .Where(p => p != Work && (File.GetAttributes(p) & FileAttributes.ReparsePoint) == 0)
                .OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
            if (previous != null)
            {
                Warnings.Add("Included an earlier interrupted recording under previous-recording/; its timestamps may describe a different session.");
                foreach (string file in Files(previous).Where(f => Path.GetExtension(f) is ".log" or ".json" or ".jsonl"))
                    AddFile("previous-recording/" + Path.GetFileName(file), file);
            }
            AddFile("logs/manager.log", DiagnosticLog.PathName);
            AddFile("logs/manager-previous.log", DiagnosticLog.PathName + ".previous");
        });
        AddText("README.txt", "AnimeJaNai support report\n\n" +
            "Start with report.json, system.json, settings/, then recording/player.log and recording/timeline.jsonl.\n" +
            "The timeline records playback time, renderer, drops, sync and subtitle settings once per second and at seeks.\n" +
            "process.jsonl and modules.json record the test process and loaded DLLs even when the player never opens.\n" +
            "markers.jsonl contains moments marked by the user. Engine builds are under engine-logs/.\n" +
            "Settings are not changed by the collector. Recording uses a separate player with extra logging.\n" +
            "No videos, model/engine binaries, watch-later files, full environment, screenshots or memory dumps are included.\n" +
            "Personal folders and common credential values are masked; file names and free-form notes may remain.\n" +
            "This ZIP is created locally. Review it before sharing. Missing data and truncation are listed in report.json.\n");
        Directory.CreateDirectory(outputDirectory);
        string destination = Path.Combine(outputDirectory, "AJN-diagnostics-" + Id + ".zip");
        progress?.Report("Creating the support ZIP…");
        var included = new List<string>();
        int total = 0;
        using (var stream = new FileStream(destination + ".partial", FileMode.Create, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            // Fresh evidence takes priority over older logs if the total limit is reached.
            foreach (var (name, text) in _documents.OrderBy(p => p.Key.StartsWith("recording/") ? 0 :
                p.Key is "system.json" or "inventory.json" or "README.txt" ? 1 : p.Key.StartsWith("settings") ? 2 : 3))
            {
                int bytes = Encoding.UTF8.GetByteCount(text);
                if (total + bytes > 32 * 1024 * 1024) { Warnings.Add($"Report size limit: skipped {name}."); continue; }
                using var writer = new StreamWriter(archive.CreateEntry(name, CompressionLevel.Fastest).Open(), new UTF8Encoding(false));
                writer.Write(text); total += bytes; included.Add(name);
            }
            var manifest = JsonSerializer.SerializeToNode(new { schema = 1, Id, StartedUtc, completed_utc = DateTime.UtcNow,
                notes = _notes, media_selected = Media != null, files = included, warnings = Warnings });
            _redactor.Json(manifest);
            using var summary = new StreamWriter(archive.CreateEntry("report.json").Open(), new UTF8Encoding(false));
            summary.Write(manifest!.ToJsonString(new() { WriteIndented = true }));
        }
        File.Move(destination + ".partial", destination);
        // Only this collector-created directory is removed, after the ZIP is
        // complete. Interrupted recordings remain available for the next report.
        try { Directory.Delete(Work, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DiagnosticLog.Error(ex); }
        return destination;
    }
}
