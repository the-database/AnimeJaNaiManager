using System;
using System.IO;
using System.Text.Json;

namespace AnimeJaNaiConfEditor.Services
{
    // The shared pack store's aji-runtime.json, which the updater has aji-runtime write beside libaji
    // (animejanai/inference): the folder libaji loads TensorRT from. Without it (an install from before
    // the store) TensorRT is in the inference folder.
    public static class RuntimePacks
    {
        // The TensorRT runtime's folder: the store's pack that aji-runtime.json names, else the
        // inference folder. Relative paths in the file are relative to it, as libaji reads them.
        public static string TrtDirectory(string inferenceDirectory)
        {
            try
            {
                string path = Path.Combine(inferenceDirectory, "aji-runtime.json");
                if (File.Exists(path))
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("trt", out var t) && t.ValueKind == JsonValueKind.String &&
                        t.GetString() is { Length: > 0 } dir)
                    {
                        return Path.GetFullPath(Path.IsPathRooted(dir) ? dir : Path.Combine(inferenceDirectory, dir));
                    }
                }
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // unreadable: as without the file
            }
            return inferenceDirectory;
        }

        // The TensorRT core library: nvinfer_11.dll on Windows, libnvinfer.so.11 on Linux.
        public static bool TrtInstalled(string inferenceDirectory) =>
            File.Exists(Path.Combine(TrtDirectory(inferenceDirectory),
                OperatingSystem.IsWindows() ? "nvinfer_11.dll" : "libnvinfer.so.11"));
    }
}
