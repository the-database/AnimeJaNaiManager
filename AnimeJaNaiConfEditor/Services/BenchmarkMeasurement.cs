using System;
using System.IO;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AnimeJaNaiConfEditor.Services;

// Written alongside the FPS table in benchmark.txt, not inferred from the
// current desktop: the user may have changed screens since the benchmark ran.
public sealed class BenchmarkMeasurement
{
    public const string Header = "Benchmark metadata: ";
    [JsonPropertyName("method")] public string Method { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("output_resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutputResolution { get; set; }
    [JsonPropertyName("display_hz")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? DisplayHz { get; set; }

    public void Validate()
    {
        if (Method != "mpv-output-trace-v2" || Mode is not ("native-display" or "pipeline"))
            throw new InvalidDataException("Unrecognized benchmark measurement method. Update the Manager and rerun the benchmark.");
        if (Mode == "native-display" &&
            (OutputResolution == null || !Regex.IsMatch(OutputResolution, @"^[1-9][0-9]{1,4}x[1-9][0-9]{1,4}$")))
            throw new InvalidDataException("The native-display benchmark is missing its measured output resolution.");
        if (Mode == "pipeline" && (OutputResolution != null || DisplayHz != null))
            throw new InvalidDataException("A pipeline benchmark cannot have an output display.");
        if (DisplayHz is double hz && (!double.IsFinite(hz) || hz <= 0 || hz > 1000))
            throw new InvalidDataException("Invalid benchmark display refresh rate.");
    }

    [JsonIgnore]
    public string Description => Mode == "native-display"
        ? $"Native-display rendering at {OutputResolution}" + (DisplayHz is double hz ? $" ({hz:0.##} Hz)" : "")
        : "Decode + upscale throughput (no display rendering)";
}
