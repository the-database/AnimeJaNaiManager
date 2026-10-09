using System;
using System.IO;

namespace AnimeJaNaiConfEditor.Services.Diagnostics;

public static class DiagnosticLog
{
    private static readonly object Sync = new();
    public static string PathName => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeJaNai", "Diagnostics", "manager.log");
    public static void Error(Exception exception)
    {
        // A logging failure must not replace the original Manager error.
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                if (new FileInfo(PathName) is { Exists: true, Length: > 256 * 1024 }) File.Move(PathName, PathName + ".previous", true);
                File.AppendAllText(PathName, DateTime.UtcNow.ToString("O") + " " + exception + "\n");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
