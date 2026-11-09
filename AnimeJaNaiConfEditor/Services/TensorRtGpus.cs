using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AnimeJaNaiConfEditor.Services
{
    // What `AnimeJaNaiUpdater --components --json` says about this PC's NVIDIA GPUs, for TensorRT:
    // Nvidia is whether one can run TensorRT; Gpus are those (name, Vulkan/CUDA device UUID), for
    // the TensorRT GPU choice; TooOld names the NVIDIA GPUs too old for TensorRT, e.g.
    // "NVIDIA GeForce GTX 1080 (compute 6.1)".
    public sealed record TensorRtGpuReport(bool Nvidia, string Name, IReadOnlyList<(string Name, string Uuid)> Gpus,
                                           IReadOnlyList<string> TooOld);

    public static class TensorRtGpus
    {
        // TensorRT needs compute capability 7.5+ (Turing: GeForce RTX 20 / GTX 16 series and newer):
        // the TensorRT 11 on CUDA 13 the mpv package ships supports nothing older, and aji's CUDA
        // kernels start at sm75. The trt-ptx pack can't help an older GPU (PTX only JIT-compiles
        // forward), so such a GPU counts as no NVIDIA GPU for TensorRT: DirectML. The updater applies
        // the same rule (mpv-AnimeJaNai AnimeJaNaiUpdater/GpuSupport.cs) and reports such GPUs in
        // `unsupported_gpus`; this reads that, and applies the rule itself to an updater from before
        // it, which counted every NVIDIA GPU.
        public static readonly (int Major, int Minor) TrtMinComputeCapability = (7, 5);

        public static bool Capable(int major, int minor) =>
            major > TrtMinComputeCapability.Major ||
            (major == TrtMinComputeCapability.Major && minor >= TrtMinComputeCapability.Minor);

        // "sm86" -> (8, 6); null when it isn't one
        public static (int Major, int Minor)? ParseSm(string sm)
        {
            var d = sm.Trim();
            if (!d.StartsWith("sm", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            d = d[2..];
            if (d.Length < 2 || !d.All(char.IsAsciiDigit) || !int.TryParse(d[..^1], out int major))
            {
                return null;
            }
            return (major, d[^1] - '0');
        }

        public static TensorRtGpuReport Read(JsonElement root)
        {
            var gpu = root.GetProperty("gpu");
            bool nvidia = gpu.GetProperty("nvidia").GetBoolean();
            string name = gpu.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            string sm = gpu.TryGetProperty("sm", out var s) ? s.GetString() ?? "" : "";
            var gpus = root.TryGetProperty("gpus", out var list) && list.ValueKind == JsonValueKind.Array
                ? list.EnumerateArray()
                    .Select(g => (Name: g.GetProperty("name").GetString() ?? "",
                                  Uuid: g.GetProperty("uuid").GetString() ?? ""))
                    .Where(g => g.Name.Length > 0 && g.Uuid.Length > 0)
                    .ToList()
                : [];
            var tooOld = new List<string>();
            if (root.TryGetProperty("unsupported_gpus", out var old) && old.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in old.EnumerateArray())
                {
                    string gn = g.TryGetProperty("name", out var x) ? x.GetString() ?? "" : "";
                    string cc = g.TryGetProperty("compute", out var c) ? c.GetString() ?? "" : "";
                    tooOld.Add(cc.Length > 0 ? $"{gn} (compute {cc})" : gn);
                }
            }
            else if (nvidia && sm.Length > 0)
            {
                // an updater from before unsupported_gpus: every NVIDIA GPU is in gpu.sm. Only when
                // none can run TensorRT is it decided here (the names aren't per GPU generation).
                var sms = sm.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(ParseSm).ToList();
                if (sms.Count > 0 && sms.All(v => v is { } p && !Capable(p.Major, p.Minor)))
                {
                    tooOld.Add($"{name} (compute {string.Join(", ", sms.Select(v => $"{v!.Value.Major}.{v.Value.Minor}"))})");
                    nvidia = false;
                    name = "";
                    gpus = [];
                }
            }
            return new TensorRtGpuReport(nvidia, name, gpus, tooOld);
        }

        // Why TensorRT can't run here, for the backend notice.
        public static string TooOldNotice(IReadOnlyList<string> tooOld) =>
            $"TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability " +
            $"{TrtMinComputeCapability.Major}.{TrtMinComputeCapability.Minor}+); {string.Join(" and ", tooOld)} " +
            $"{(tooOld.Count == 1 ? "is" : "are")} too old for it.";

        // The Components tab's GPU line.
        public static string GpuText(TensorRtGpuReport r, bool directMl)
        {
            string old = string.Join(" and ", r.TooOld);
            if (r.Nvidia)
            {
                return r.TooOld.Count == 0
                    ? $"GPU: {r.Name}"
                    : $"GPU: {r.Name} ({old}: too old for TensorRT, not used for it)";
            }
            if (r.TooOld.Count > 0)
            {
                return directMl
                    ? $"GPU: {old} — too old for TensorRT, which needs a GeForce RTX 20 / GTX 16 series or newer GPU; the built-in DirectML engine runs on it"
                    : $"GPU: {old} — too old for TensorRT, which needs a GeForce RTX 20 / GTX 16 series or newer GPU and is the only upscaling engine on Linux";
            }
            return "GPU: no NVIDIA device detected — the built-in DirectML engine covers AMD and Intel GPUs";
        }
    }
}
