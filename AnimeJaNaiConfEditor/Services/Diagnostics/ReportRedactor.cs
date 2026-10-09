using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AnimeJaNaiConfEditor.Services.Diagnostics;

// Applied to every exported text file, including collector failures and user notes.
// This is deliberately not described as anonymization: media names may remain.
public sealed class ReportRedactor
{
    private readonly List<(string Path, string Alias)> _paths = [];
    private const string SecretKey = @"(?:password|passwd|pwd|secret|token|access[_-]?token|refresh[_-]?token|api[_-]?key|authorization|cookie|x-plex-token)";

    public ReportRedactor(params (string Path, string Alias)[] paths)
    {
        foreach (var item in paths.Append((Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<user>")))
        {
            if (string.IsNullOrWhiteSpace(item.Item1)) continue;
            foreach (string variant in new[] { item.Item1, item.Item1.Replace('\\', '/'), item.Item1.Replace('/', '\\') }.Distinct())
                _paths.Add((variant.TrimEnd('/', '\\'), item.Item2));
        }
        _paths.Sort((a, b) => b.Path.Length.CompareTo(a.Path.Length));
    }

    public string Text(string text)
    {
        foreach (var (path, alias) in _paths)
            text = text.Replace(path, alias, StringComparison.OrdinalIgnoreCase);
        // Other accounts can appear in copied configs and engine logs.
        text = Regex.Replace(text, @"(?i)([A-Z]:[/\\]Users[/\\])[^/\\\s""<>]+", "$1<user>");
        text = Regex.Replace(text, @"(?i)(/home/)[^/\s""<>]+", "$1<user>");
        text = Regex.Replace(text, @"(?im)^(\s*(?:--)?[^\r\n=:\s]*" + SecretKey + @"[^\r\n=:\s]*\s*[=:]).*$", "$1 <redacted>");
        text = Regex.Replace(text, @"(?i)(""[^""\r\n]*" + SecretKey + @"[^""\r\n]*""\s*:\s*)""(?:\\.|[^""\\])*""", "$1\"<redacted>\"");
        text = Regex.Replace(text, @"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9+/=_\-.]+", "$1 <redacted>");
        text = Regex.Replace(text, @"(?im)(\b(?:authorization|cookie|set-cookie|x-plex-token)\s*:\s*)[^\r\n]+", "$1<redacted>");
        // URL queries commonly hold Plex tokens, signed media URLs, and cookies.
        text = Regex.Replace(text, @"(?i)(https?://[^\s""<>?]+)\?[^\s""<>]+", "$1?<redacted>");
        text = Regex.Replace(text, @"(?i)(https?://)[^/\s""<>@]+@", "$1<redacted>@");
        text = Regex.Replace(text, @"(?i)((?:--|\b)[\w-]*" + SecretKey + @"[\w-]*=)(?:""[^""]*""|'[^']*'|[^\s,;]+)", "$1<redacted>");
        return text;
    }

    public void Json(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            foreach (var pair in obj.ToArray())
            {
                if (Regex.IsMatch(pair.Key, SecretKey, RegexOptions.IgnoreCase))
                    obj[pair.Key] = "<redacted>";
                else if (pair.Value is JsonValue v && v.TryGetValue<string>(out var s)) obj[pair.Key] = Text(s);
                else Json(pair.Value);
            }
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
                if (array[i] is JsonValue v && v.TryGetValue<string>(out var s)) array[i] = Text(s);
                else Json(array[i]);
        }
    }
}
