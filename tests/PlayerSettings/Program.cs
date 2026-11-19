using AnimeJaNaiConfEditor.Services;
using AnimeJaNaiConfEditor.Services.PlayerSettings;
using AnimeJaNaiConfEditor.ViewModels;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Player settings page: mpv.conf editing, the option catalog, live apply over the player's IPC,
// and the Manager's launch arguments.
//
//   dotnet run --project tests/PlayerSettings [-- --mpv <mpv.com|mpv> [--portable-config <dir>]]
//
// --mpv (or MPV_EXE) checks the catalog against that player's --list-options and drives a real
// player with a scratch --config-dir; without it those checks are skipped. --portable-config
// round-trips a package's mpv.conf and mpv-animejanai.conf (copied to a scratch folder first).
string? mpv = Environment.GetEnvironmentVariable("MPV_EXE");
string? package = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--mpv") mpv = args[i + 1];
    if (args[i] == "--portable-config") package = args[i + 1];
}

int passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception("FAIL " + message); }
void Pass(string message) { passed++; Console.WriteLine("PASS " + message); }

string scratch = Path.Combine(Path.GetTempPath(), "ajn-player-settings-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);
UiState.Load(scratch);
var catalog = OptionCatalog.Embedded;
PlayerOption O(string name) => catalog.Find(name) ?? throw new Exception("not in the catalog: " + name);

const string Managed = """
    # AnimeJaNai managed settings - DO NOT EDIT.
    profile-restore=copy-equal
    [animejanai]
    vo=gpu-next
    profile=high-quality
    gpu-api=vulkan,auto
    hwdec=nvdec
    hr-seek=yes
    osd-font = 'Segoe UI'
    save-position-on-quit=yes
    cache=yes
    cache-secs=30
    demuxer-max-bytes=768M
    demuxer-max-back-bytes=384M
    volume=100
    volume-max=100
    blend-subtitles=video
    alang = 'jpn'
    slang = 'eng'
    deband=yes
    screenshot-format=png
    screenshot-directory=~~/screenshots/
    screenshot-template='%F__%P__%04n'
    profile=upscale-on

    [extension.gif]
    loop-file=inf
    [subs-gpu]
    blend-subtitles=no
    [upscale-on]
    vf=@aji:animejanai
    """;

const string Shipped = """
    # Your mpv settings. Safe to edit - updates never overwrite this file.

    include="~~/mpv-animejanai.conf"
    profile = animejanai

    # ===== Your settings below =====
    # Examples - uncomment to enable:
    #fs                     # start fullscreen
    #volume-max=130        # allow boosting volume above 100% (overrides default)

    """;

// A portable_config folder with these two files; returns the loaded config.
PlayerConfig Fixture(string mpvConf, string? managed = Managed, string newline = "\n", bool bom = false)
{
    string dir = Path.Combine(scratch, Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    if (managed != null) File.WriteAllText(Path.Combine(dir, PlayerConfig.ManagedFileName), managed.Replace("\n", newline));
    if (mpvConf.Length > 0 || bom) File.WriteAllText(Path.Combine(dir, "mpv.conf"), mpvConf.Replace("\n", newline), new UTF8Encoding(bom));
    return PlayerConfig.Load(catalog, dir);
}
string Text(PlayerConfig c) => File.ReadAllText(c.MpvConfPath);
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void Set(PlayerConfig c, string option, string value) { c.Reload(); if (c.Set(O(option), value)) c.Save(); }

// ---- mpv.conf round trips ---------------------------------------------------------------
foreach (string nl in new[] { "\n", "\r\n" })
{
    string label = nl == "\n" ? "LF" : "CRLF";
    var c = Fixture(Shipped, newline: nl);
    string original = Text(c);
    string managedHash = Hash(c.ManagedConfPath);
    Check(c.Document.ToString() == original, $"{label}: unchanged document doesn't serialize back byte for byte");
    Check(c.BaseValue(O("volume-max")) == "100" && c.BaseValue(O("scale")) == "ewa_lanczossharp" && c.BaseValue(O("deband")) == "yes",
        $"{label}: defaults don't come from the managed profile (and profile=high-quality)");
    Check(c.BaseValue(O("interpolation")) == "no" && c.BaseValue(O("screenshot-dir")) == "~~/screenshots/", $"{label}: mpv default / alias in the managed file");
    Set(c, "volume-max", "150");
    Check(Text(c) == original.Replace("#volume-max=130        # allow boosting volume above 100% (overrides default)" + nl,
        "#volume-max=130        # allow boosting volume above 100% (overrides default)" + nl + "volume-max=150" + nl),
        $"{label}: new line not added after the user's last line with the file's line ending:\n" + Text(c));
    Set(c, "deband", "yes");
    Check(!Text(c).Contains("deband"), $"{label}: a value equal to the default added a line");
    Set(c, "volume-max", "100");
    Check(Text(c) == original, $"{label}: setting the default back didn't restore the file exactly:\n" + Text(c));
    Check(Hash(c.ManagedConfPath) == managedHash, $"{label}: mpv-animejanai.conf was written");
    Pass($"{label}: shipped mpv.conf round trip, comments and include line kept, default removes the line, managed file untouched");
}

{
    // Comments, unknown options and order survive; an existing line is changed in place
    var c = Fixture("include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\n# mine\nvideo-sync=display-resample   # smooth\nfoo-unknown=1\ninterpolation\ntscale=oversample\n");
    Check(c.UserValue(O("interpolation")) == "yes" && c.Effective(O("video-sync")) == "display-resample", "bare flag / commented value not read");
    Set(c, "video-sync", "display-tempo");
    Set(c, "interpolation", "no");
    Check(Text(c) == "include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\n# mine\nvideo-sync=display-tempo   # smooth\nfoo-unknown=1\ntscale=oversample\n",
        "in-place edit lost a comment, an unknown option or the order:\n" + Text(c));
    Pass("comments, unknown options and order kept; values changed in place with their trailing comment");
}

{
    // No newline at the end of the file stays that way; a UTF-8 BOM stays
    var c = Fixture("include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nontop=yes", newline: "\r\n", bom: true);
    Set(c, "border", "no");
    byte[] bytes = File.ReadAllBytes(c.MpvConfPath);
    Check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM lost");
    Check(Text(c) == "include=\"~~/mpv-animejanai.conf\"\r\nprofile = animejanai\r\nontop=yes\r\nborder=no", "missing final newline not kept:\n" + Text(c));
    Pass("CRLF without a final newline and a UTF-8 BOM kept");
}

{
    // Older file: include but no profile line. The profile line goes in after the include, as
    // the updater's migration does, and the user's line after it.
    var c = Fixture("# old\ninclude=\"~~/mpv-animejanai.conf\"\nfs\n");
    Check(!c.HasProfileLine && c.BaseValue(O("volume-max")) == "100" && c.UserValue(O("fullscreen")) == "yes", "missing profile line: defaults or user value wrong");
    Set(c, "ontop", "yes");
    Check(Text(c) == "# old\ninclude=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nfs\nontop=yes\n", "profile line not added after the include:\n" + Text(c));
    Pass("missing profile line: added after the include, user lines after it");
}

{
    // No include at all (a hand-written or pre-3.4 file): every top-level line is the user's,
    // defaults are mpv's own, and nothing is added at the top.
    var c = Fixture("vo=gpu-next\nvolume-max=100\n\n# loop gifs\n[extension.gif]\nloop-file=inf\n");
    Check(!c.HasInclude && c.BaseValue(O("volume-max")) == "130" && c.UserValue(O("volume-max")) == "100", "no include: mpv defaults expected");
    Set(c, "volume-max", "130");
    Set(c, "cache", "yes");
    Check(Text(c) == "vo=gpu-next\ncache=yes\n\n# loop gifs\n[extension.gif]\nloop-file=inf\n", "no include: wrong edit:\n" + Text(c));
    Pass("no include line: mpv's defaults, lines placed before a [profile] and its comment");
}

{
    // Options inside a [profile] aren't the user's top-level value; [default] goes back to it, so
    // a new line goes after the user's last top-level line there
    var c = Fixture("include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\n\n[extension.webm]\nfullscreen=yes\nontop=yes\n[default]\nontop=no\n");
    Check(c.UserValue(O("fullscreen")) == null && c.UserValue(O("ontop")) == "no", "[profile] or [default] lines read wrong");
    Set(c, "fullscreen", "yes");
    Set(c, "ontop", "yes");
    Check(Text(c) == "include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\n\n[extension.webm]\nfullscreen=yes\nontop=yes\n[default]\nontop=yes\nfullscreen=yes\n",
        "edit went into a [profile]:\n" + Text(c));
    Pass("[section] lines left alone, [default] treated as top level");
}

{
    // Duplicates: the last wins; a change keeps only the last; a reset removes them all
    var c = Fixture("include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nvolume=50\nsub-scale=1.2\nvolume=70\n");
    Check(c.UserValue(O("volume")) == "70", "duplicate: last should win");
    Set(c, "volume", "80");
    Check(Text(c) == "include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nsub-scale=1.2\nvolume=80\n", "duplicate change:\n" + Text(c));
    c = Fixture("include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nvolume=50\nvolume=70\n");
    Set(c, "volume", "100");
    Check(!Text(c).Contains("volume"), "reset left a duplicate that would come back");
    Pass("duplicates: last wins, change keeps one line, reset removes all");
}

{
    // no- flags, aliases and bare flags
    var c = Fixture("include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nno-border\nfs=no  # start windowed\nno-cache-pause\n");
    Check(c.UserValue(O("border")) == "no" && c.UserValue(O("fullscreen")) == "no" && c.UserValue(O("cache-pause")) == "no", "no- flags or alias not read");
    Set(c, "fullscreen", "yes");
    Set(c, "border", "yes");
    Set(c, "cache-pause", "no");
    Check(Text(c) == "include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nfs=yes  # start windowed\nno-cache-pause\n", "no-/alias edit:\n" + Text(c));
    c = Fixture("include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nno-ontop\n");
    Set(c, "ontop", "yes");
    Check(Text(c).EndsWith("\nontop=yes\n"), "no-<flag> line not turned into <flag>=yes:\n" + Text(c));
    Pass("no-<flag>, aliases (fs) and bare flags read and written");
}

{
    // Quoted values, spaces around '=', values that need quoting
    var c = Fixture("include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nsub-font = 'Segoe UI'\nscreenshot-template=\"%F #%n\"\n");
    Check(c.UserValue(O("sub-font")) == "Segoe UI" && c.UserValue(O("screenshot-template")) == "%F #%n", "quoted values not read");
    Set(c, "sub-font", "Noto Sans");
    Set(c, "alang", "jpn,ja");
    Set(c, "screenshot-template", "it's \"%n\" # x");
    Check(c.Document.Lines.Any(l => l.Text == "sub-font = 'Noto Sans'") && c.Document.Lines.Any(l => l.Text == "alang=jpn,ja"), "quote style not kept:\n" + Text(c));
    c.Reload();
    Check(c.UserValue(O("screenshot-template")) == "it's \"%n\" # x", "both quotes: %len% form didn't read back: " + Text(c));
    Set(c, "screenshot-template", "%F__%P__%04n");
    Check(!Text(c).Contains("screenshot-template"), "quoted managed default not recognised");
    Pass("quoted values: read, style kept, %len% form for both quote kinds, managed quoted default");
}

{
    // Same value in another spelling counts as the default: 768MiB = 768M, 1.0 = 1
    var c = Fixture(Shipped);
    Set(c, "demuxer-max-bytes", "768MiB");
    Set(c, "sub-scale", "1.0");
    Check(!Text(c).Contains("demuxer-max-bytes") && !Text(c).Contains("sub-scale"), "equal values in another spelling added lines");
    Check(O("demuxer-max-bytes").Validate("2GiB") is null && O("demuxer-max-bytes").Validate("lots") is not null &&
          O("volume").Validate("abc") is not null && O("screenshot-jpeg-quality").Validate("50.5") is not null &&
          O("volume-max").Validate("50") is not null, "validation");
    Pass("byte sizes and numbers compared by value; invalid input rejected");
}

{
    // No mpv.conf yet: created like the package's, with the include and profile lines
    var c = Fixture("");
    Check(!c.FileExists && c.BaseValue(O("volume-max")) == "100", "missing mpv.conf: managed defaults expected");
    Set(c, "ontop", "yes");
    Check(Text(c).Replace("\r\n", "\n") == "include=\"~~/mpv-animejanai.conf\"\nprofile = animejanai\nontop=yes\n", "new mpv.conf:\n" + Text(c));
    Pass("missing mpv.conf created with the include and profile lines");
}

{
    // The page without a player: saved to the file only; a file that can't be written is
    // reported and the page shows what the file still says
    var c = Fixture(Shipped);
    var vm = new PlayerSettingsViewModel(c);
    var ontop = vm.Find("ontop")!;
    ontop.FlagValue = true;
    await vm.Idle;
    Check(Text(c).Contains("\nontop=yes\n") && ontop.IsSet && vm.Status.Contains("Saved") && !vm.StatusIsError, "page without a player: " + vm.Status);
    var hidden = vm.Find("screenshot-format")!;
    hidden.SelectedChoice = hidden.Choices.First(x => x.Value == "webp");
    await vm.Idle;
    Check(Text(c).Contains("\nscreenshot-format=webp\n"), "page: choice not saved");
    // an editable list: a picked entry saves its value, typed text saves as typed (a typed label
    // as its value), empty text is refused, and a reset leaves no line
    var pointer = vm.Find("cursor-autohide")!;
    Check(pointer.Text == "1000 ms", "editable list shows the default's label");
    pointer.Pick(pointer.Choices.First(x => x.Value == "no"));
    await vm.Idle;
    Check(Text(c).Contains("\ncursor-autohide=no\n") && pointer.Text == "Never", "editable list: picked entry not saved as its value");
    pointer.Text = "2500";
    await vm.Idle;
    Check(Text(c).Contains("\ncursor-autohide=2500\n"), "editable list: typed value not saved");
    pointer.Text = "Always hidden";
    await vm.Idle;
    Check(Text(c).Contains("\ncursor-autohide=always\n"), "editable list: typed label not saved as its value");
    pointer.Text = "";
    await vm.Idle;
    Check(pointer.HasError && Text(c).Contains("\ncursor-autohide=always\n"), "editable list: empty text must be refused");
    pointer.Reset();
    await vm.Idle;
    Check(!Text(c).Contains("cursor-autohide") && pointer.Text == "1000 ms" && !pointer.HasError, "editable list: reset: " + Text(c));
    // (Windows: a read-only file can't be replaced; on Linux the folder's permissions decide)
    if (OperatingSystem.IsWindows())
    {
        File.SetAttributes(c.MpvConfPath, FileAttributes.ReadOnly);
        try
        {
            ontop.FlagValue = false;
            await vm.Idle;
            Check(vm.StatusIsError && vm.Status.StartsWith("Could not save mpv.conf", StringComparison.Ordinal) && ontop.FlagValue && ontop.IsSet,
                "page: a read-only mpv.conf must be reported and the old value shown: " + vm.Status);
        }
        finally { File.SetAttributes(c.MpvConfPath, FileAttributes.Normal); }
        Check(!File.Exists(c.MpvConfPath + ".tmp"), "a failed save left mpv.conf.tmp behind");
    }
    vm.Filter = "screenshot";
    Check(vm.Sections.Single(s => s.IsVisible).Name == "Screenshots" && vm.Sections.Single(s => s.IsVisible).IsExpanded, "search: only matching sections, opened");
    vm.Filter = "";
    Check(vm.Sections.All(s => s.IsVisible) && vm.Items.All(i => i.IsVisible), "search cleared");
    Pass("page without a player: saved to the file, write errors reported, search");
}

if (package != null)
{
    // The real package files: unchanged round trip and a set/reset cycle
    var c = Fixture(File.ReadAllText(Path.Combine(package, "mpv.conf")), File.ReadAllText(Path.Combine(package, PlayerConfig.ManagedFileName)));
    string original = Text(c);
    Check(c.Document.ToString() == original && c.HasInclude && c.HasProfileLine && c.ManagedFound, "package mpv.conf layout not recognised");
    var defaults = catalog.Options.Where(o => !o.SameValue(c.BaseValue(o), o.Default)).Select(o => $"{o.Name}={c.BaseValue(o)}");
    Console.WriteLine("     managed defaults that differ from mpv's: " + string.Join(", ", defaults));
    foreach (var option in catalog.Options.Where(o => o.Type == OptionType.Flag)) Set(c, option.Name, PlayerOption.FlagValue(c.BaseValue(option)) == "yes" ? "no" : "yes");
    foreach (var option in catalog.Options.Where(o => o.Type == OptionType.Flag)) Set(c, option.Name, c.BaseValue(option));
    Check(Text(c) == original, "package mpv.conf not restored after toggling every flag and back:\n" + Text(c));
    Pass("package mpv.conf and mpv-animejanai.conf: layout found, every flag toggled and restored byte for byte");
}

// ---- catalog --------------------------------------------------------------------------
{
    var names = new HashSet<string>();
    foreach (var o in catalog.Options)
    {
        Check(names.Add(o.Name) && o.Aliases.All(names.Add), "duplicate option " + o.Name);
        Check(catalog.Sections.Contains(o.Section), o.Name + ": unknown section " + o.Section);
        Check(o.Help.Length is > 20 and < 220 && !o.Help.Contains('\n') && o.Label.Length > 0, o.Name + ": help must be one line");
        Check(o.ManualUrl.StartsWith("https://mpv.io/manual/", StringComparison.Ordinal), o.Name + ": manual link");
        if (o.Type == OptionType.Choice)
        {
            Check(o.Choices.Count > 1 && o.Choices.Select(c => c.Value).Distinct().Count() == o.Choices.Count, o.Name + ": choices");
            Check(o.Editable || o.Default == "" || o.Choices.Any(c => c.Value == o.Default), o.Name + ": mpv's default isn't a choice");
        }
        Check(o.Validate(o.Default) is null || o.Default == "", o.Name + ": default doesn't validate");
    }
    Check(catalog.Options.Count is >= 35 and <= 60, "about 40 options expected");
    foreach (string section in catalog.Sections) Check(catalog.Options.Any(o => o.Section == section), "empty section " + section);
    Pass($"catalog: {catalog.Options.Count} options in {catalog.Sections.Count} sections, unique, one-line help, valid defaults");
}

string? player = null;
if (mpv is null) Console.WriteLine("SKIP catalog against mpv and live IPC: pass --mpv <path> or set MPV_EXE");
else
{
    mpv = Path.GetFullPath(mpv);
    string Run(params string[] arguments)
    {
        var info = new ProcessStartInfo(mpv) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string a in arguments) info.ArgumentList.Add(a);
        using var p = Process.Start(info)!;
        string output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return output;
    }
    // " --name    Type (default: value)" and " --alias    alias for name"
    var listed = new Dictionary<string, string>();
    foreach (string line in Run("--no-config", "--list-options").Split('\n'))
    {
        var m = System.Text.RegularExpressions.Regex.Match(line.TrimEnd('\r'), @"^ --(\S+)\s+(.*)$");
        if (m.Success) listed[m.Groups[1].Value] = m.Groups[2].Value;
    }
    Check(listed.Count > 500, "mpv --list-options gave no list: " + mpv);
    foreach (var o in catalog.Options)
    {
        Check(listed.TryGetValue(o.Name, out string? description), o.Name + " is not an mpv option");
        foreach (string alias in o.Aliases) Check(listed.TryGetValue(alias, out string? a) && a.Trim() == "alias for " + o.Name, alias + " is not an alias of " + o.Name);
        string type = description!.Split(' ')[0];
        string[] expected = o.Type switch
        {
            OptionType.Flag => ["Flag"],
            OptionType.Number => o.Integer ? ["Integer"] : ["Float", "Double", "Integer"],
            OptionType.Bytes => ["ByteSize"],
            OptionType.Choice => ["Choices:", "Object", "Audio", "String"],
            _ => ["String", "Window"],
        };
        Check(expected.Contains(type), $"{o.Name}: catalog type {o.Type}, mpv says {description}");
        var d = System.Text.RegularExpressions.Regex.Match(description, @"\(default: (.*)\)( \[file\])?$");
        Check(d.Success && o.SameValue(o.Default, d.Groups[1].Value.Trim()), $"{o.Name}: catalog default '{o.Default}', mpv says '{d.Groups[1].Value}'");
        if (type == "Choices:")
        {
            var values = description[8..description.IndexOf("(default:", StringComparison.Ordinal)].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            bool integers = description.Contains("(or an integer)");
            foreach (var c in o.Choices)
                Check(values.Contains(c.Value) || integers && int.TryParse(c.Value, out _), $"{o.Name}: '{c.Value}' is not one of mpv's choices");
        }
    }
    foreach (var (profile, values) in catalog.BuiltinProfiles)
    {
        var shown = Run("--no-config", "--show-profile=" + profile).Split('\n').Select(l => l.Trim()).Where(l => l.Contains('='))
            .ToDictionary(l => l[..l.IndexOf('=')], l => l[(l.IndexOf('=') + 1)..]);
        Check(shown.Count == values.Count && values.All(v => shown.TryGetValue(v.Key, out string? s) && s == v.Value),
            $"built-in profile {profile} differs: mpv has {string.Join(", ", shown.Select(s => s.Key + "=" + s.Value))}");
    }
    Pass($"catalog against {Path.GetFileName(mpv)}: every option and alias exists, types, defaults, choices and built-in profiles match");

    // on Windows, mpv.com waits on mpv.exe; drive mpv.exe itself
    player = Path.GetExtension(mpv).Equals(".com", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.ChangeExtension(mpv, ".exe"))
        ? Path.ChangeExtension(mpv, ".exe") : mpv;
}

// ---- live apply over IPC against a real player ----------------------------------------
if (player != null)
{
    string configDir = Path.Combine(scratch, "player-config");
    Directory.CreateDirectory(configDir);
    string endpoint = OperatingSystem.IsWindows() ? @"\\.\pipe\ajn-player-settings-test-" + Guid.NewGuid().ToString("N")[..8]
        : Path.Combine(scratch, "player.sock");
    var info = new ProcessStartInfo(player) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (string a in new[] { "--idle=yes", "--no-terminal", "--force-window=no", "--config-dir=" + configDir, "--input-ipc-server=" + endpoint })
        info.ArgumentList.Add(a);
    using var process = Process.Start(info)!;
    try
    {
        async Task<string?> Get(string property)
        {
            for (int attempt = 0; attempt < 50; attempt++)
            {
                try
                {
                    using var cancel = new CancellationTokenSource(2000);
                    Stream stream;
                    if (PlayerIpc.PipeName(endpoint) is { } pipe)
                    {
                        var client = new System.IO.Pipes.NamedPipeClientStream(".", pipe, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
                        await client.ConnectAsync(cancel.Token);
                        stream = client;
                    }
                    else
                    {
                        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                        await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(endpoint), cancel.Token);
                        stream = new System.Net.Sockets.NetworkStream(socket, true);
                    }
                    await using (stream)
                    {
                        await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command = new[] { "get_property_string", property }, request_id = 7 }) + "\n"), cancel.Token);
                        using var reader = new StreamReader(stream);
                        while (await reader.ReadLineAsync(cancel.Token) is { } line)
                        {
                            using var reply = JsonDocument.Parse(line);
                            if (reply.RootElement.TryGetProperty("request_id", out var id) && id.GetInt32() == 7)
                                return reply.RootElement.GetProperty("data").GetString();
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException) { await Task.Delay(100); }
            }
            throw new Exception("the test player never answered on " + endpoint);
        }
        _ = await Get("mpv-version");

        Check(await PlayerIpc.SetAsync(endpoint, "volume", "42") is null && double.Parse(await Get("volume") ?? "", System.Globalization.CultureInfo.InvariantCulture) == 42, "set volume over IPC");
        Check(await PlayerIpc.SetAsync(endpoint, "video-sync", "not-a-mode") is { } refused && refused.Length > 0, "an invalid value must report the player's error");
        string missing = OperatingSystem.IsWindows() ? @"\\.\pipe\ajn-no-such-player-" + Guid.NewGuid().ToString("N") : Path.Combine(scratch, "none.sock");
        Check(await PlayerIpc.SetAsync(missing, "volume", "1") == PlayerIpc.NotRunning, "a missing player must read as not running");
        Pass("IPC: set reaches the real player, refusals and a missing player are reported");

        // every live option takes its default and each of its choices on the real player
        var failed = new List<string>();
        foreach (var o in catalog.Options.Where(o => o.Live && o.AvailableHere))
        {
            var values = new List<string> { o.Default };
            values.AddRange(o.Choices.Select(c => c.Value));
            if (o.Type == OptionType.Flag) values.AddRange(["yes", "no"]);
            foreach (string value in values.Distinct())
                if (await PlayerIpc.SetAsync(endpoint, o.Name, value) is { } error) failed.Add($"{o.Name}={value}: {error}");
        }
        Check(failed.Count == 0, "the player refused: " + string.Join("; ", failed));
        Pass("IPC: every live option accepts its default and every listed value");

        // the page end to end: a change is written to mpv.conf and applied to the player
        var c = Fixture(Shipped);
        var vm = new PlayerSettingsViewModel(c);
        PlayerIpc.Endpoint = endpoint;
        var item = vm.Find("deband")!;
        Check(item.FlagValue && !item.IsSet, "page shows the managed default");
        item.FlagValue = false;
        await vm.Idle;
        Check(Text(c).Contains("\ndeband=no\n") && item.IsSet && await Get("deband") == "no" && !vm.StatusIsError, "page: deband=no not saved and applied: " + vm.Status);
        item.Reset();
        await vm.Idle;
        Check(!Text(c).Contains("deband=") && !item.IsSet && await Get("deband") == "yes", "page: reset didn't remove the line and apply the default");
        var volume = vm.Find("volume-max")!;
        volume.Text = "250";
        await vm.Idle;
        Check(Text(c).Contains("\nvolume-max=250\n") && await Get("volume-max") == "250.000000", "page: number not saved and applied: " + await Get("volume-max"));
        volume.Text = "lots";
        await vm.Idle;
        Check(volume.HasError && Text(c).Contains("\nvolume-max=250\n"), "page: invalid number must not be written");
        var gpuApi = vm.Find("gpu-api")!;
        gpuApi.SelectedChoice = gpuApi.Choices.First(x => x.Value == "auto");
        await vm.Idle;
        Check(Text(c).Contains("\ngpu-api=auto\n") && vm.Status.Contains("restarts"), "page: a restart-only option must be saved and say so: " + vm.Status);
        Pass("page: changes saved to mpv.conf and applied live, reset, invalid input, restart-only option");

        // the player closes while the page is open: the next change only goes to the file
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        vm.Find("ontop")!.FlagValue = true;
        await vm.Idle;
        Check(Text(c).Contains("\nontop=yes\n") && PlayerIpc.Endpoint == null && !vm.StatusIsError && vm.Status.Contains("next time"),
            "page: a closed player must not be an error: " + vm.Status);
        Pass("page: after the player closes, changes are saved for the next start");
    }
    finally
    {
        // only the scratch player started above
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
    }
}

// ---- launch arguments -----------------------------------------------------------------
{
    var a = LaunchArgs.Parse(["--page", "Player", "--player-ipc", @"\\.\pipe\mpv-animejanai-1a2b3c4d", "--owner-window", "1312", "--future-flag", "--future", "x", "file.mkv"]);
    Check(a == new LaunchArgs("player", @"\\.\pipe\mpv-animejanai-1a2b3c4d", 1312), "space-separated arguments: " + a);
    a = LaunchArgs.Parse(["--page=components", "--player-ipc=/run/user/1000/mpv-animejanai-1a2b3c4d.sock", "--owner-window=0x1A2B"]);
    Check(a == new LaunchArgs("components", "/run/user/1000/mpv-animejanai-1a2b3c4d.sock", 0x1A2B), "= arguments: " + a);
    Check(LaunchArgs.Parse(["--page", "--player-ipc"]).IsEmpty && LaunchArgs.Parse(["--owner-window", "nope"]).IsEmpty && LaunchArgs.Parse([]).IsEmpty,
        "missing or bad values must be ignored");
    var message = new LaunchArgs("player", @"\\.\pipe\x", 99).ToActivationMessage();
    Check(message[0] == 1 && LaunchArgs.FromActivationPayload(message.AsSpan(1)) == new LaunchArgs("player", @"\\.\pipe\x", 99), "activation message round trip");
    Check(LaunchArgs.FromActivationPayload([]).IsEmpty && LaunchArgs.FromActivationPayload("not json"u8).IsEmpty &&
          LaunchArgs.FromActivationPayload("{\"page\":5,\"extra\":true}"u8).IsEmpty, "an old (one-byte) or odd activation message must mean no arguments");
    Check(LaunchArgs.None.ToActivationMessage()[0] == 1, "first byte must stay 1 for older Managers");
    Pass("launch arguments: --page/--player-ipc/--owner-window in both forms, unknown ones ignored, activation message compatible");
}

try { Directory.Delete(scratch, true); } catch { }
Console.WriteLine($"ALL PLAYER SETTINGS CHECKS PASSED ({passed})");
