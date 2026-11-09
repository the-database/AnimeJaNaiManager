// Which NVIDIA GPUs count for TensorRT, from the updater's --components --json, with and without
// its unsupported_gpus (an updater from before it counted every NVIDIA GPU). Exits nonzero on failure.
using System.Text.Json;
using AnimeJaNaiConfEditor.Services;

int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) failures++;
}
TensorRtGpuReport Read(string json)
{
    using var doc = JsonDocument.Parse(json);
    return TensorRtGpus.Read(doc.RootElement);
}

Check(TensorRtGpus.TrtMinComputeCapability == (7, 5), "the threshold is compute capability 7.5");
Check(!TensorRtGpus.Capable(6, 1) && !TensorRtGpus.Capable(7, 0) && TensorRtGpus.Capable(7, 5) && TensorRtGpus.Capable(12, 0),
    "Pascal and Volta are too old, Turing and newer aren't");
Check(TensorRtGpus.ParseSm("sm61") == (6, 1) && TensorRtGpus.ParseSm("sm120") == (12, 0) && TensorRtGpus.ParseSm("x") == null,
    "sm names");

// the updater with unsupported_gpus: a GTX 1080 alone
var r = Read("""{"gpu":{"nvidia":false,"sm":"","name":""},"gpus":[],"unsupported_gpus":[{"name":"NVIDIA GeForce GTX 1080","uuid":"a","compute":"6.1"}],"packs":[]}""");
Check(!r.Nvidia && r.Gpus.Count == 0, "GTX 1080: no NVIDIA GPU for TensorRT");
Check(r.TooOld.SequenceEqual(["NVIDIA GeForce GTX 1080 (compute 6.1)"]), "GTX 1080: named as too old");
Check(TensorRtGpus.TooOldNotice(r.TooOld) ==
      "TensorRT needs an NVIDIA GeForce RTX 20 / GTX 16 series or newer GPU (compute capability 7.5+); " +
      "NVIDIA GeForce GTX 1080 (compute 6.1) is too old for it.", "the notice");
Check(TensorRtGpus.GpuText(r, true).Contains("DirectML engine runs on it"), "the GPU line on Windows");
Check(TensorRtGpus.GpuText(r, false).Contains("only upscaling engine on Linux"), "the GPU line on Linux");

// a mixed PC: only the old GPU is left out
r = Read("""{"gpu":{"nvidia":true,"sm":"sm86","name":"RTX 3080"},"gpus":[{"name":"RTX 3080","uuid":"b"}],"unsupported_gpus":[{"name":"GTX 1080","uuid":"a","compute":"6.1"}],"packs":[]}""");
Check(r.Nvidia && r.Gpus.Count == 1 && r.Gpus[0].Uuid == "b", "GTX 1080 + RTX 3080: TensorRT on the RTX 3080");
Check(TensorRtGpus.GpuText(r, true) == "GPU: RTX 3080 (GTX 1080 (compute 6.1): too old for TensorRT, not used for it)", "the mixed GPU line");

// an updater from before unsupported_gpus reported the GTX 1080 as an NVIDIA GPU
r = Read("""{"gpu":{"nvidia":true,"sm":"sm61","name":"GTX 1080"},"gpus":[{"name":"GTX 1080","uuid":"a"}],"packs":[]}""");
Check(!r.Nvidia && r.Gpus.Count == 0 && r.TooOld.SequenceEqual(["GTX 1080 (compute 6.1)"]), "older updater, GTX 1080: too old");
r = Read("""{"gpu":{"nvidia":true,"sm":"sm61, sm70","name":"GTX 1080 + V100"},"gpus":[],"packs":[]}""");
Check(!r.Nvidia && r.TooOld.SequenceEqual(["GTX 1080 + V100 (compute 6.1, 7.0)"]), "older updater, two old GPUs");
r = Read("""{"gpu":{"nvidia":true,"sm":"sm61, sm86","name":"GTX 1080 + RTX 3080"},"gpus":[],"packs":[]}""");
Check(r.Nvidia && r.TooOld.Count == 0, "older updater, mixed: TensorRT stays");
r = Read("""{"gpu":{"nvidia":true,"sm":"sm75","name":"GTX 1660"},"gpus":[],"packs":[]}""");
Check(r.Nvidia && r.TooOld.Count == 0 && TensorRtGpus.GpuText(r, true) == "GPU: GTX 1660", "GTX 1660 (7.5) runs TensorRT");
r = Read("""{"gpu":{"nvidia":false,"sm":"","name":""},"packs":[]}""");
Check(!r.Nvidia && r.TooOld.Count == 0 && TensorRtGpus.GpuText(r, true).Contains("no NVIDIA device"), "no NVIDIA GPU");

Console.WriteLine(failures == 0 ? "tensorrt gpu tests passed" : $"{failures} failed");
return failures == 0 ? 0 : 1;
