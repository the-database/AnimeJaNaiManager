// TensorRT's folder from the shared pack store's aji-runtime.json (the Manager's "TensorRT installed"
// check). Exits nonzero on failure.
using AnimeJaNaiConfEditor.Services;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

string root = Path.Combine(Path.GetTempPath(), $"ajm-runtime-packs-{Guid.NewGuid():N}");
string inference = Path.Combine(root, "inference");
string store = Path.Combine(root, "store", "trt-runtime", "11.4");
string nvinfer = OperatingSystem.IsWindows() ? "nvinfer_11.dll" : "libnvinfer.so.11";
Directory.CreateDirectory(inference);
Directory.CreateDirectory(store);
try
{
    // without the file: the inference folder
    Check(RuntimePacks.TrtDirectory(inference) == inference, "no file: the inference folder");
    Check(!RuntimePacks.TrtInstalled(inference), "nothing installed yet");
    File.WriteAllText(Path.Combine(inference, nvinfer), "x");
    Check(RuntimePacks.TrtInstalled(inference), "TensorRT in the inference folder (before the store)");
    File.Delete(Path.Combine(inference, nvinfer));

    // the file names the store's pack
    File.WriteAllText(Path.Combine(inference, "aji-runtime.json"),
        "{\"format\": 1, \"trt\": " + System.Text.Json.JsonSerializer.Serialize(store) + "}");
    Check(RuntimePacks.TrtDirectory(inference) == Path.GetFullPath(store), "the store's TensorRT pack");
    Check(!RuntimePacks.TrtInstalled(inference), "the named pack is empty");
    File.WriteAllText(Path.Combine(store, nvinfer), "x");
    Check(RuntimePacks.TrtInstalled(inference), "TensorRT in the store's pack");

    // relative to the file's folder, as libaji reads it
    File.WriteAllText(Path.Combine(inference, "aji-runtime.json"), "{\"format\": 1, \"trt\": \"../store/trt-runtime/11.4\"}");
    Check(RuntimePacks.TrtDirectory(inference) == Path.GetFullPath(store), "a relative path");

    // an unreadable file counts as no file
    File.WriteAllText(Path.Combine(inference, "aji-runtime.json"), "{ not json");
    Check(RuntimePacks.TrtDirectory(inference) == inference, "an unreadable file");
    Console.WriteLine("runtime packs tests passed");
}
finally
{
    Directory.Delete(root, true);
}
