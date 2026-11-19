using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AnimeJaNaiConfEditor.Services.PlayerSettings;

public enum ConfLineKind { Blank, Comment, Section, Option }

// One line of an mpv config file, split the way mpv reads it (m_config_parse in mpv's
// options/parse_configfile.c) so a changed value can be put back without touching the rest of
// the line: indentation, the key as written, the spacing around '=', quotes and a trailing
// comment all survive.
public sealed class ConfLine
{
    public string Text { get; private set; }
    public string Ending { get; set; }
    public ConfLineKind Kind { get; private set; }
    // Section lines: the profile name between the brackets.
    public string? SectionName { get; private set; }
    // Option lines.
    public string Key { get; private set; } = "";
    public string? Value { get; private set; }
    // Inside a [profile] (false) or at the top level, which [default] returns to (true).
    public bool TopLevel { get; internal set; } = true;

    private string _indent = "", _prefix = "", _separator = "", _trailing = "";
    private char? _quote;

    public ConfLine(string text, string ending)
    {
        Text = text;
        Ending = ending;
        Parse();
    }

    public static string FormatValue(string value, char? preferredQuote = null)
    {
        bool needsQuotes = value.Length == 0 || value.Contains('#') || value[0] is '"' or '\'' or '%' ||
                           char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]) || preferredQuote != null;
        if (!needsQuotes) return value;
        foreach (char q in preferredQuote is { } p ? new[] { p, '"', '\'' } : new[] { '"', '\'' })
            if (!value.Contains(q)) return q + value + q;
        // Both quote characters: mpv's length-prefixed form, %<bytes>%value.
        return "%" + Encoding.UTF8.GetByteCount(value) + "%" + value;
    }

    // Puts a new value on this option line; key is what to write if the line's own spelling can't
    // stay (a no-<flag> line gets its plain name).
    public void SetValue(string key, string value)
    {
        string separator = Value is null ? "=" : _separator;
        Text = _indent + _prefix + key + separator + FormatValue(value, _quote) + _trailing;
        Parse();
    }

    private void Parse()
    {
        string text = Text;
        int i = 0;
        while (i < text.Length && text[i] is ' ' or '\t') i++;
        _indent = text[..i];
        if (i == text.Length) { Kind = ConfLineKind.Blank; return; }
        if (text[i] == '#') { Kind = ConfLineKind.Comment; return; }
        if (text[i] == '[')
        {
            Kind = ConfLineKind.Section;
            int close = text.IndexOf(']', i + 1);
            SectionName = (close < 0 ? text[(i + 1)..] : text[(i + 1)..close]).Trim();
            return;
        }
        Kind = ConfLineKind.Option;
        _prefix = "";
        if (string.CompareOrdinal(text, i, "--", 0, 2) == 0) { _prefix = "--"; i += 2; }
        int keyStart = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '=' and not '#') i++;
        Key = text[keyStart..i];
        int j = i;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        _quote = null;
        if (j >= text.Length || text[j] != '=')
        {
            Value = null;
            _separator = "";
            _trailing = text[i..];
            return;
        }
        j++;
        while (j < text.Length && char.IsWhiteSpace(text[j])) j++;
        _separator = text[i..j];
        int end;
        if (j < text.Length && text[j] is '"' or '\'')
        {
            char q = text[j];
            int close = text.IndexOf(q, j + 1);
            if (close < 0) { Value = text[(j + 1)..]; end = text.Length; }
            else { Value = text[(j + 1)..close]; end = close + 1; _quote = q; }
        }
        else if (j < text.Length && text[j] == '%' && TryLengthQuoted(text, j, out string? quoted, out end))
        {
            Value = quoted;
        }
        else
        {
            int hash = text.IndexOf('#', j);
            Value = text[j..(hash < 0 ? text.Length : hash)].TrimEnd();
            end = j + Value.Length;
        }
        _trailing = text[end..];
    }

    private static bool TryLengthQuoted(string text, int start, out string? value, out int end)
    {
        value = null; end = start;
        int second = text.IndexOf('%', start + 1);
        if (second < 0 || !int.TryParse(text.AsSpan(start + 1, second - start - 1), out int bytes) || bytes < 0) return false;
        string rest = text[(second + 1)..];
        byte[] utf8 = Encoding.UTF8.GetBytes(rest);
        if (bytes > utf8.Length) return false;
        value = Encoding.UTF8.GetString(utf8, 0, bytes);
        end = second + 1 + value.Length;
        return true;
    }
}

// An mpv config file as a list of lines. Serializing an unchanged document gives back the same
// bytes: line endings (CRLF, LF, mixed, none at the end) and a UTF-8 byte order mark are kept.
public sealed class MpvConfDocument
{
    public List<ConfLine> Lines { get; } = [];
    public bool ByteOrderMark { get; set; }
    // For lines the Manager adds: the file's own line ending.
    public string Newline { get; set; } = OperatingSystem.IsWindows() ? "\r\n" : "\n";

    public static MpvConfDocument Parse(string text)
    {
        var document = new MpvConfDocument();
        if (text.StartsWith('﻿')) { document.ByteOrderMark = true; text = text[1..]; }
        int crlf = 0, lf = 0, start = 0;
        while (start < text.Length)
        {
            int newline = text.IndexOf('\n', start);
            if (newline < 0) { document.Lines.Add(new ConfLine(text[start..], "")); break; }
            bool cr = newline > start && text[newline - 1] == '\r';
            if (cr) crlf++; else lf++;
            document.Lines.Add(new ConfLine(text[start..(cr ? newline - 1 : newline)], cr ? "\r\n" : "\n"));
            start = newline + 1;
        }
        if (crlf + lf > 0) document.Newline = crlf >= lf ? "\r\n" : "\n";
        document.MarkTopLevel();
        return document;
    }

    public static MpvConfDocument Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        bool bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var document = Parse(new UTF8Encoding(false).GetString(bytes, bom ? 3 : 0, bytes.Length - (bom ? 3 : 0)));
        document.ByteOrderMark = bom;
        return document;
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var line in Lines) builder.Append(line.Text).Append(line.Ending);
        return builder.ToString();
    }

    // Writes next to the file and moves it over, so a crash or a full disk never leaves half a
    // mpv.conf behind.
    public void Save(string path)
    {
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, ToString(), new UTF8Encoding(ByteOrderMark));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // Which lines mpv applies at the top level: before the first [profile] header, and after a
    // [default] header, which returns to it.
    public void MarkTopLevel()
    {
        bool top = true;
        foreach (var line in Lines)
        {
            if (line.Kind == ConfLineKind.Section) top = string.Equals(line.SectionName, "default", StringComparison.Ordinal);
            line.TopLevel = top && line.Kind != ConfLineKind.Section;
        }
    }

    public void Insert(int index, string text)
    {
        var line = new ConfLine(text, Newline);
        if (index == Lines.Count && Lines.Count > 0 && Lines[^1].Ending == "")
        {
            // the file had no newline at the end: keep it that way
            Lines[^1].Ending = Newline;
            line.Ending = "";
        }
        Lines.Insert(index, line);
        MarkTopLevel();
    }

    public void RemoveAt(int index)
    {
        bool last = index == Lines.Count - 1;
        string ending = Lines[index].Ending;
        Lines.RemoveAt(index);
        if (last && Lines.Count > 0 && ending == "") Lines[^1].Ending = "";
        MarkTopLevel();
    }

    // The sections defined in the file, each as its lines in order (for the managed file).
    public Dictionary<string, List<ConfLine>> Sections()
    {
        var sections = new Dictionary<string, List<ConfLine>>(StringComparer.Ordinal);
        List<ConfLine>? current = null;
        foreach (var line in Lines)
        {
            if (line.Kind == ConfLineKind.Section)
            {
                current = line.SectionName == "default" ? null
                    : sections.TryGetValue(line.SectionName!, out var existing) ? existing : sections[line.SectionName!] = [];
                continue;
            }
            if (current != null && line.Kind == ConfLineKind.Option) current.Add(line);
        }
        return sections;
    }

    public IEnumerable<ConfLine> TopLevelOptions() => Lines.Where(l => l.TopLevel && l.Kind == ConfLineKind.Option);
}
