using System.Text.Json;
using AnimeJaNaiConfEditor.Services;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

string file = Path.GetTempFileName();
try
{
    const string table = "\n|fps|768x576|1920x1080|\n|-|-|-|\n|Balanced|448.70|138.46|\n|Performance|571.98|229.74|\n";
    const string title = "AnimeJaNai benchmark v2 - backend: TensorRT\n";
    string header(string json) => BenchmarkMeasurement.Header + json + "\n";
    foreach (string resolution in new[] { "2560x1440", "3840x2160" })
    {
        var measurement = new { method = "mpv-output-trace-v2", mode = "native-display", output_resolution = resolution, display_hz = 143.997 };
        File.WriteAllText(file, title + header(JsonSerializer.Serialize(measurement)) + table);
        var sub = BenchmarkSubmission.FromBenchmarkFile(file);
        Check(sub.Schema == 2 && sub.Backend == "TensorRT", "Parse v2 schema and backend.");
        Check(sub.Results["Balanced"]["1920x1080"] == 138.46, "Preserve canonical table cells.");
        using var json = JsonDocument.Parse(sub.ToPreviewJson());
        Check(json.RootElement.GetProperty("measurement").GetProperty("output_resolution").GetString() == resolution,
            "Keep the measured screen, independently of the machine submitting the file.");
        Check(sub.Measurement!.Description.Contains(resolution), "Show output resolution in the submission preview.");
    }
    File.WriteAllText(file, title + header("""{"method":"mpv-output-trace-v2","mode":"pipeline"}""") + table);
    var pipeline = BenchmarkSubmission.FromBenchmarkFile(file);
    Check(pipeline.Measurement!.OutputResolution == null && pipeline.Schema == 2, "Pipeline has no display target.");

    File.WriteAllText(file, "AnimeJaNai playback benchmark - backend: TensorRT\n" + table);
    var legacy = BenchmarkSubmission.FromBenchmarkFile(file);
    Check(legacy.Schema == 1 && legacy.Measurement == null, "Legacy must not inherit current screen or stale metadata.");
    Check(!legacy.ToPreviewJson().Contains("measurement"), "Omit legacy metadata.");

    foreach (string metadata in new[] { "", header("""{"method":"mpv-output-trace-v2","mode":"native-display"}""") })
    {
        File.WriteAllText(file, title + metadata + table);
        try { BenchmarkSubmission.FromBenchmarkFile(file); throw new Exception("Incomplete v2 data must not be submitted as legacy."); }
        catch (InvalidDataException) { }
    }
    // Optional actual runner artifacts exercise the same parser without sending data.
    foreach (string actual in args)
    {
        var sub = BenchmarkSubmission.FromBenchmarkFile(actual);
        Check(sub.Schema == 2 && sub.HasResults, "Actual runner result must be a complete v2 submission.");
        Console.WriteLine(sub.Measurement!.Description);
    }
    Console.WriteLine("Benchmark submission checks passed.");
}
finally { File.Delete(file); }
