using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AnimeJaNaiConfEditor.Services.PlayerSettings;

// The player settings in portable_config: mpv.conf (the user's file) read together with the
// managed mpv-animejanai.conf it includes.
//
// mpv.conf from the package:
//     include="~~/mpv-animejanai.conf"     managed file, replaced by every update
//     profile = animejanai                 applies the managed defaults
//     ...the user's own lines...           applied after them, so they win
//
// Only the user's part is edited: top-level lines after `profile = animejanai` (all top-level
// lines when the file has no include). Comments, options the Manager doesn't know, their order
// and the line endings stay as they are. A value equal to what the lines before the user's part
// give (the default shown) removes the user's line. mpv-animejanai.conf is never written.
public sealed class PlayerConfig
{
    public const string ManagedFileName = "mpv-animejanai.conf";
    public const string ManagedProfile = "animejanai";

    public OptionCatalog Catalog { get; }
    public string MpvConfPath { get; }
    public string ManagedConfPath { get; }
    public MpvConfDocument Document { get; private set; }
    public bool FileExists { get; private set; }
    // The include of mpv-animejanai.conf and the `profile = animejanai` line, as found.
    public bool HasInclude => IncludeIndex >= 0;
    public bool HasProfileLine => ProfileIndex >= 0;
    public bool ManagedFound => _managedSections.ContainsKey(ManagedProfile);

    private readonly List<ConfLine> _managedTopLevel = [];
    private readonly Dictionary<string, List<ConfLine>> _managedSections = new(StringComparer.Ordinal);
    private Dictionary<string, string> _base = new(StringComparer.Ordinal);

    private PlayerConfig(OptionCatalog catalog, string mpvConf, string managedConf)
    {
        Catalog = catalog; MpvConfPath = mpvConf; ManagedConfPath = managedConf;
        Document = new MpvConfDocument();
    }

    public static PlayerConfig Load(OptionCatalog catalog, string portableConfigDir) =>
        Load(catalog, Path.Combine(portableConfigDir, "mpv.conf"), Path.Combine(portableConfigDir, ManagedFileName));

    public static PlayerConfig Load(OptionCatalog catalog, string mpvConf, string managedConf)
    {
        var config = new PlayerConfig(catalog, mpvConf, managedConf);
        config.Reload();
        return config;
    }

    // Reads both files again: the user may have edited mpv.conf by hand since.
    public void Reload()
    {
        _managedTopLevel.Clear(); _managedSections.Clear();
        if (File.Exists(ManagedConfPath))
        {
            var managed = MpvConfDocument.Load(ManagedConfPath);
            _managedTopLevel.AddRange(managed.TopLevelOptions());
            foreach (var (name, lines) in managed.Sections()) _managedSections[name] = lines;
        }
        FileExists = File.Exists(MpvConfPath);
        if (FileExists) Document = MpvConfDocument.Load(MpvConfPath);
        else
        {
            // A missing mpv.conf is created the way the package ships it, once something is set.
            Document = new MpvConfDocument();
            if (ManagedFound)
            {
                Document.Insert(0, $"include=\"~~/{ManagedFileName}\"");
                Document.Insert(1, $"profile = {ManagedProfile}");
            }
        }
        ComputeBase();
    }

    private int IncludeIndex => Document.Lines.FindIndex(l => l.TopLevel && l.Kind == ConfLineKind.Option &&
        l.Key == "include" && (l.Value ?? "").Replace('\\', '/').EndsWith(ManagedFileName, StringComparison.OrdinalIgnoreCase));

    private int ProfileIndex => Document.Lines.FindIndex(l => l.TopLevel && l.Kind == ConfLineKind.Option &&
        l.Key == "profile" && (l.Value ?? "").Trim() == ManagedProfile);

    // The first line of the user's part: after the profile line; after the include when the
    // profile line is missing (it's added there, as the updater's migration does); else the
    // whole file.
    private int UserStart => HasProfileLine ? ProfileIndex + 1 : HasInclude ? IncludeIndex + 1 : 0;

    // The option a line sets and its value: aliases (fs = fullscreen), no-<flag> = <flag>=no, a
    // bare flag = yes.
    public (PlayerOption Option, string Value)? Resolve(ConfLine line)
    {
        if (line.Kind != ConfLineKind.Option) return null;
        if (Catalog.Find(line.Key) is { } option)
            return (option, line.Value ?? (option.Type == OptionType.Flag || option.Choices.Any(c => c.Value == "yes") ? "yes" : ""));
        if (line.Key.StartsWith("no-", StringComparison.Ordinal) && line.Value is null &&
            Catalog.Find(line.Key[3..]) is { } negated && (negated.Type == OptionType.Flag || negated.Choices.Any(c => c.Value == "no")))
            return (negated, "no");
        return null;
    }

    private void Apply(Dictionary<string, string> values, IEnumerable<ConfLine> lines, int depth)
    {
        foreach (var line in lines)
        {
            if (line.Kind != ConfLineKind.Option) continue;
            if (line.Key == "profile") { ApplyProfiles(values, line.Value ?? "", depth); continue; }
            if (Resolve(line) is { } set) values[set.Option.Name] = set.Value;
        }
    }

    private void ApplyProfiles(Dictionary<string, string> values, string names, int depth)
    {
        if (depth > 10) return;
        foreach (string name in names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (_managedSections.TryGetValue(name, out var lines)) Apply(values, lines, depth + 1);
            else if (Catalog.BuiltinProfiles.TryGetValue(name, out var builtin))
                foreach (var (key, value) in builtin)
                    if (Catalog.Find(key) is { } option) values[option.Name] = value;
        }
    }

    // What every option is before the user's part: mpv's default, then the lines above it in
    // order (the include's top-level lines, the managed profile).
    private void ComputeBase()
    {
        var values = Catalog.Options.ToDictionary(o => o.Name, o => o.Default, StringComparer.Ordinal);
        int start = UserStart;
        bool profilePending = !HasProfileLine && ManagedFound;
        for (int i = 0; i < start; i++)
        {
            var line = Document.Lines[i];
            if (!line.TopLevel || line.Kind != ConfLineKind.Option) continue;
            if (i == IncludeIndex)
            {
                Apply(values, _managedTopLevel, 0);
                if (profilePending) ApplyProfiles(values, ManagedProfile, 0);
            }
            else if (line.Key == "profile") ApplyProfiles(values, line.Value ?? "", 0);
            else if (Resolve(line) is { } set) values[set.Option.Name] = set.Value;
        }
        _base = values;
    }

    // The default the page shows: what the player uses without a line of the user's.
    public string BaseValue(PlayerOption option) => _base.GetValueOrDefault(option.Name, option.Default);

    private List<int> UserLines(PlayerOption option)
    {
        var found = new List<int>();
        for (int i = UserStart; i < Document.Lines.Count; i++)
            if (Document.Lines[i].TopLevel && Resolve(Document.Lines[i]) is { } set && set.Option == option) found.Add(i);
        return found;
    }

    // The user's own value (the last line wins, as in mpv), or null when they haven't set it.
    public string? UserValue(PlayerOption option)
    {
        var lines = UserLines(option);
        return lines.Count == 0 ? null : Resolve(Document.Lines[lines[^1]])!.Value.Value;
    }

    public string Effective(PlayerOption option) => UserValue(option) ?? BaseValue(option);

    public bool IsSet(PlayerOption option) => UserLines(option).Count > 0;

    // Changes the document; Save writes it. Returns whether anything changed.
    public bool Set(PlayerOption option, string value)
    {
        value = value.Trim();
        var lines = UserLines(option);
        if (option.SameValue(value, BaseValue(option)))
        {
            if (lines.Count == 0) return false;
            for (int k = lines.Count - 1; k >= 0; k--) Document.RemoveAt(lines[k]);
            return true;
        }
        if (lines.Count > 0)
        {
            var last = Document.Lines[lines[^1]];
            if (Resolve(last) is { } current && option.SameValue(current.Value, value) && lines.Count == 1) return false;
            // keep the user's spelling (fs, screenshot-directory) unless it was no-<flag>
            string key = Catalog.Find(last.Key) == option ? last.Key : option.Name;
            last.SetValue(key, value);
            // earlier duplicates did nothing (the last line won); without them a later reset
            // can't bring an old value back
            for (int k = lines.Count - 2; k >= 0; k--) Document.RemoveAt(lines[k]);
            return true;
        }
        EnsureProfileLine();
        Document.Insert(InsertionPoint(), option.Name + "=" + ConfLine.FormatValue(value));
        return true;
    }

    // Older mpv.conf files have the include but not the profile line (before 3.5 the managed
    // defaults were top-level). Add it where the updater's migration does, so the user's line
    // comes after the defaults and wins.
    private void EnsureProfileLine()
    {
        if (HasProfileLine || !HasInclude || !ManagedFound) return;
        Document.Insert(IncludeIndex + 1, $"profile = {ManagedProfile}");
    }

    // After the user's last option line; with none, at the end of the top-level lines that
    // follow the profile line, before a [profile] header and the comments directly above it.
    private int InsertionPoint()
    {
        int start = UserStart;
        int lastOption = -1;
        for (int i = start; i < Document.Lines.Count; i++)
            if (Document.Lines[i].TopLevel && Document.Lines[i].Kind == ConfLineKind.Option) lastOption = i;
        if (lastOption >= 0) return lastOption + 1;
        int end = start;
        while (end < Document.Lines.Count && Document.Lines[end].Kind != ConfLineKind.Section) end++;
        int stop = end;
        if (end < Document.Lines.Count)
            while (stop > start && Document.Lines[stop - 1].Kind == ConfLineKind.Comment) stop--;
        int lastContent = start - 1;
        for (int i = start; i < stop; i++)
            if (Document.Lines[i].Kind != ConfLineKind.Blank) lastContent = i;
        return lastContent + 1;
    }

    public void Save()
    {
        string? folder = Path.GetDirectoryName(MpvConfPath);
        if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
        Document.Save(MpvConfPath);
        FileExists = true;
        ComputeBase();
    }
}
