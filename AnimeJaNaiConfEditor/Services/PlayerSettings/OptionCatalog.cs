using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AnimeJaNaiConfEditor.Services.PlayerSettings;

public enum OptionType { Flag, Choice, Number, Text, Folder, Bytes }

public sealed record OptionChoice(string Value, string Label);

// One mpv option the Player page offers. The help texts are our own; the manual link goes to
// mpv's manual for the details.
public sealed class PlayerOption
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public required string Section { get; init; }
    public required string Label { get; init; }
    public required OptionType Type { get; init; }
    // mpv's own default (what the player uses when no config file sets the option).
    public required string Default { get; init; }
    public IReadOnlyList<OptionChoice> Choices { get; init; } = [];
    public bool Editable { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public bool Integer { get; init; }
    // Sent to a running player with `set` when the Manager knows its IPC endpoint.
    public bool Live { get; init; }
    public string? Os { get; init; }
    public string? Placeholder { get; init; }
    public required string Help { get; init; }
    public required string ManualUrl { get; init; }

    public bool AvailableHere => Os is null ||
        (Os == "windows" && OperatingSystem.IsWindows()) || (Os == "linux" && OperatingSystem.IsLinux());

    // Same value as far as mpv is concerned: "768M" = "768MiB", "1.0" = "1", a bare flag = "yes".
    public bool SameValue(string? a, string? b)
    {
        if (a is null || b is null) return a is null && b is null;
        a = a.Trim(); b = b.Trim();
        switch (Type)
        {
            case OptionType.Flag:
                return FlagValue(a) == FlagValue(b);
            case OptionType.Number:
                return TryNumber(a, out double x) && TryNumber(b, out double y) ? x == y : a == b;
            case OptionType.Bytes:
                return TryBytes(a, out double p) && TryBytes(b, out double q) ? p == q : a == b;
            default:
                if (Type == OptionType.Choice && TryNumber(a, out double m) && TryNumber(b, out double n)) return m == n;
                return a == b;
        }
    }

    // A value the player would accept, or why not. Empty text and list options are fine.
    public string? Validate(string value)
    {
        value = value.Trim();
        switch (Type)
        {
            case OptionType.Number:
                if (!TryNumber(value, out double v)) return "Enter a number.";
                if (Integer && v != Math.Floor(v)) return "Enter a whole number.";
                if (Min is { } min && v < min) return FormattableString.Invariant($"The smallest value is {min}.");
                if (Max is { } max && v > max) return FormattableString.Invariant($"The largest value is {max}.");
                return null;
            case OptionType.Bytes:
                return TryBytes(value, out _) ? null : "Enter a size such as 768MiB or 2GiB.";
            case OptionType.Choice when !Editable:
                return Choices.Any(c => c.Value == value) ? null : "Choose one of the listed values.";
            case OptionType.Choice when value.Length == 0:
                return "Enter a value or pick one from the list.";
            default:
                return value.Contains('\n') || value.Contains('\r') ? "Enter a single line." : null;
        }
    }

    internal static string FlagValue(string value) => value is "" or "yes" ? "yes" : value;

    public static bool TryNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    // mpv byte sizes: a number with an optional binary suffix (K/KiB, M/MiB, G/GiB, T/TiB).
    public static bool TryBytes(string text, out double bytes)
    {
        bytes = 0;
        var match = System.Text.RegularExpressions.Regex.Match(text, @"^\s*([0-9]*\.?[0-9]+(?:[eE][+-]?[0-9]+)?)\s*([A-Za-z]*)\s*$");
        if (!match.Success || !TryNumber(match.Groups[1].Value, out double number)) return false;
        string unit = match.Groups[2].Value.ToLowerInvariant();
        double scale = unit switch
        {
            "" or "b" => 1,
            "k" or "kib" => 1024,
            "m" or "mib" => 1024d * 1024,
            "g" or "gib" => 1024d * 1024 * 1024,
            "t" or "tib" => 1024d * 1024 * 1024 * 1024,
            _ => -1,
        };
        if (scale < 0) return false;
        bytes = number * scale;
        return true;
    }
}

public sealed class OptionCatalog
{
    public IReadOnlyList<string> Sections { get; }
    public IReadOnlyList<PlayerOption> Options { get; }
    // mpv's built-in profiles the managed profile applies (profile=high-quality), so the defaults
    // shown include what they set.
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> BuiltinProfiles { get; }
    private readonly Dictionary<string, PlayerOption> _byName = new(StringComparer.Ordinal);

    private OptionCatalog(IReadOnlyList<string> sections, IReadOnlyList<PlayerOption> options,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> profiles)
    {
        Sections = sections; Options = options; BuiltinProfiles = profiles;
        foreach (var option in options)
        {
            _byName[option.Name] = option;
            foreach (string alias in option.Aliases) _byName[alias] = option;
        }
    }

    public PlayerOption? Find(string name) => _byName.GetValueOrDefault(name);

    private static OptionCatalog? _embedded;
    public static OptionCatalog Embedded => _embedded ??= Load(
        typeof(OptionCatalog).Assembly.GetManifestResourceStream("AnimeJaNaiManager.PlayerSettings.player-options.json")
        ?? throw new InvalidOperationException("The player option catalog is missing."));

    public static OptionCatalog Load(Stream json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = document.RootElement;
        string manual = root.GetProperty("manual").GetString()!;
        var sections = root.GetProperty("sections").EnumerateArray().Select(s => s.GetString()!).ToList();
        var profiles = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        if (root.TryGetProperty("builtin_profiles", out var builtin))
            foreach (var profile in builtin.EnumerateObject())
                profiles[profile.Name] = profile.Value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!);
        var options = new List<PlayerOption>();
        foreach (var o in root.GetProperty("options").EnumerateArray())
        {
            string name = o.GetProperty("option").GetString()!;
            string? Str(string key) => o.TryGetProperty(key, out var v) ? v.GetString() : null;
            double? Num(string key) => o.TryGetProperty(key, out var v) ? v.GetDouble() : null;
            bool Bool(string key) => o.TryGetProperty(key, out var v) && v.GetBoolean();
            var choices = new List<OptionChoice>();
            if (o.TryGetProperty("choices", out var list))
                foreach (var c in list.EnumerateArray())
                {
                    if (c.ValueKind == JsonValueKind.String) { choices.Add(new(c.GetString()!, c.GetString()!)); continue; }
                    if (c.TryGetProperty("os", out var os) && os.GetString() == "windows" && !OperatingSystem.IsWindows()) continue;
                    string value = c.GetProperty("value").GetString()!;
                    choices.Add(new(value, c.TryGetProperty("label", out var l) ? l.GetString()! : value));
                }
            options.Add(new PlayerOption
            {
                Name = name,
                Aliases = o.TryGetProperty("aliases", out var aliases) ? aliases.EnumerateArray().Select(a => a.GetString()!).ToList() : [],
                Section = o.GetProperty("section").GetString()!,
                Label = o.GetProperty("label").GetString()!,
                Type = Enum.Parse<OptionType>(o.GetProperty("type").GetString()!, ignoreCase: true),
                Default = Str("default") ?? "",
                Choices = choices,
                Editable = Bool("editable"),
                Min = Num("min"), Max = Num("max"), Integer = Bool("integer"),
                Live = Bool("live"),
                Os = Str("os"),
                Placeholder = Str("placeholder"),
                Help = o.GetProperty("help").GetString()!,
                ManualUrl = manual + (Str("manual") ?? name),
            });
        }
        return new OptionCatalog(sections, options, profiles);
    }
}
