// Components > Storage: reading `aji-runtime location --json`, where a move goes, when the section
// shows and which buttons it offers, and that a store whose folder is missing doesn't switch the
// backend to DirectML. With AJI_RUNTIME_EXE (an aji-runtime 0.2.0+ build) it also drives the real
// tool on a store in a temporary folder. Exits nonzero on failure.
using AnimeJaNaiConfEditor.Services;
using AnimeJaNaiConfEditor.ViewModels;
using ReactiveUI.Builder;

static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

string dir = Path.Combine(Path.GetTempPath(), $"ajm-storage-{Guid.NewGuid():N}");
string root = Path.Combine(dir, "install");
string data = Path.Combine(root, "animejanai");
string inference = Path.Combine(data, "inference");
string fakeTool = Path.Combine(dir, "not-aji-runtime.exe");
Directory.CreateDirectory(inference);
Directory.CreateDirectory(Path.Combine(data, "onnx"));
File.WriteAllText(Path.Combine(data, "animejanai.conf"), "[global]\nconfig_version=3\nbackend=TensorRT\n");
// before the view model's paths are read
Environment.SetEnvironmentVariable("ANIMEJANAI_DATA_DIR", data);
Environment.SetEnvironmentVariable("ANIMEJANAI_ROOT", root);
string? oldStore = Environment.GetEnvironmentVariable("AJI_RUNTIME_STORE");
int skipped = 0;
try
{
    RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();

    // location --json
    {
        Check(StoreLocation.Parse("""{"root": "C:\\a", "moved": false, "target": null, "available": true}""") == new StoreLocation.Info(@"C:\a", false, null, true), "a default store");
        var moved = StoreLocation.Parse("""{"root": "C:\\a", "moved": true, "target": "F:\\b", "available": false}""")!;
        Check(moved.Folder == @"F:\b" && moved.Missing && !moved.MovedHere, "a moved store whose folder is missing");
        Check(StoreLocation.Parse("not json") is null && StoreLocation.Parse("[1]") is null, "unreadable output is no answer");
        Console.WriteLine("PASS location --json is read");
    }

    // a move goes into a folder of its own when the picked one has files
    {
        string target = Path.Combine(dir, "target");
        Directory.CreateDirectory(target);
        Check(StoreLocation.MoveTarget(target) == target, "an empty folder");
        Check(StoreLocation.MoveTarget(Path.Combine(target, "new")) == Path.Combine(target, "new"), "a new folder");
        File.WriteAllText(Path.Combine(target, "mine.txt"), "x");
        Check(StoreLocation.MoveTarget(target) == Path.Combine(target, "JaNai shared runtime"), "a folder with files");
        Check(StoreLocation.Size(target) == 1 && StoreLocation.Size(Path.Combine(dir, "nowhere")) == 0, "sizes");
        Console.WriteLine("PASS where a move goes");
    }

    // shown only where the player uses the store: aji-runtime at the install root and aji-runtime.json
    {
        Check(!StoreLocation.UsedBy(root, data), "no aji-runtime, no aji-runtime.json");
        StoreLocation.AjiRuntimeOverride = fakeTool;
        File.WriteAllText(fakeTool, "");
        Check(!StoreLocation.UsedBy(root, data), "aji-runtime but no aji-runtime.json (no TensorRT in the store, or before the store)");
        File.WriteAllText(Path.Combine(inference, "aji-runtime.json"), "{}");
        Check(StoreLocation.UsedBy(root, data), "both");
        StoreLocation.AjiRuntimeOverride = null;
        Check(!StoreLocation.UsedBy(root, data), "aji-runtime.json without aji-runtime (the AppImage)");
        File.Delete(Path.Combine(inference, "aji-runtime.json"));
        Console.WriteLine("PASS shown where the player uses the store");
    }

    // The view model: built while nothing uses the store, so its startup checks finish at once.
    var vm = new MainWindowViewModel();
    Check(!vm.ShowStorage && vm.Storage is null, "nothing to show");
    // (no TensorRT anywhere yet, so on Windows it switched to DirectML: back to the fixture's TensorRT)
    vm.AnimeJaNaiConf.BackendAutoFallback = false;
    vm.AnimeJaNaiConf.SetTensorRtSelected();

    // The install's TensorRT is in the store (aji-runtime.json names a pack there), which is missing.
    StoreLocation.AjiRuntimeOverride = fakeTool;
    string storeRoot = Path.Combine(dir, "store");
    string trtPack = Path.Combine(storeRoot, "packs", "trt-runtime", "11.4");
    File.WriteAllText(Path.Combine(inference, "aji-runtime.json"),
        "{\"format\": 1, \"trt\": " + System.Text.Json.JsonSerializer.Serialize(trtPack) + "}");
    Check(!MainWindowViewModel.TrtOnDisk(), "TensorRT looks missing");

    // the buttons follow where the store is
    {
        vm.Storage = new StoreLocation.Info(storeRoot, false, null, true);
        Check(vm.ShowStorage && vm.StorageFolderText == storeRoot && !vm.StorageMoved && !vm.StorageMissing, "default: Move...");
        vm.Storage = new StoreLocation.Info(storeRoot, true, @"F:\aji", true);
        Check(vm.StorageFolderText == @"F:\aji" && vm.StorageMoved && !vm.StorageMissing, "moved: Move..., Move Back to Default");
        vm.Storage = new StoreLocation.Info(storeRoot, true, @"F:\aji", false);
        Check(!vm.StorageMoved && vm.StorageMissing && vm.StorageText.Contains("isn't available"), "missing: Try Again, Reset to Default");
        Check(vm.StorageMissingBanner.Contains(@"F:\aji") && !vm.ShowSetupNeeded, "the missing folder, not the setup banner");
        Console.WriteLine("PASS the buttons follow where the store is");
    }

    // A missing store is not "TensorRT isn't installed": the backend stays, and the notice says why.
    {
        vm.RefreshComponentAwareness();
        Check(vm.AnimeJaNaiConf.TensorRtSelected, "a missing store switched the backend");
        Check(vm.BackendNotice.Contains(@"F:\aji isn't available") && vm.BackendNotice.Contains("Components > Storage"), "notice: " + vm.BackendNotice);
        // once the store is back at the default path but empty (after a reset), TensorRT really is missing
        vm.Storage = new StoreLocation.Info(storeRoot, false, null, true);
        vm.RefreshComponentAwareness();
        Check(!vm.BackendNotice.Contains("isn't available"), "notice after a reset: " + vm.BackendNotice);
        if (OperatingSystem.IsWindows())
        {
            Check(vm.AnimeJaNaiConf.DirectMlSelected && vm.AnimeJaNaiConf.BackendAutoFallback, "TensorRT really missing: DirectML, switching back by itself");
            vm.AnimeJaNaiConf.BackendAutoFallback = false;
            vm.AnimeJaNaiConf.SetTensorRtSelected();
        }
        Console.WriteLine("PASS a missing store keeps the backend");
    }

    // Against a real aji-runtime, on a store in a temporary folder: what the Storage buttons run.
    string? exe = Environment.GetEnvironmentVariable("AJI_RUNTIME_EXE");
    if (!File.Exists(exe))
    {
        Console.WriteLine("SKIP aji-runtime integration: set AJI_RUNTIME_EXE to an aji-runtime 0.2.0+ build");
        skipped++;
    }
    else
    {
        Directory.CreateDirectory(Path.Combine(storeRoot, "packs", "trt-runtime", "11.4"));
        File.WriteAllText(Path.Combine(storeRoot, "store.json"), """{"format": 1}""");
        string nvinfer = OperatingSystem.IsWindows() ? "nvinfer_11.dll" : "libnvinfer.so.11";
        File.WriteAllBytes(Path.Combine(trtPack, nvinfer), new byte[1_000_000]);
        Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", storeRoot);
        StoreLocation.AjiRuntimeOverride = exe;

        Check(await StoreLocation.GetAsync(root) == new StoreLocation.Info(storeRoot, false, null, true), "a default store");
        await vm.RefreshStorage();
        Check(vm.ShowStorage && !vm.StorageMoved && !vm.StorageMissing && MainWindowViewModel.TrtOnDisk(), "the section, read through aji-runtime");

        string dest = Path.Combine(dir, "other-drive");
        var progress = new List<(long, long)>();
        Check(await StoreLocation.ChangeAsync(root, ["move", dest], (d, t) => progress.Add((d, t))) is null, "move");
        Check(progress.Count > 0 && progress[^1].Item1 == progress[^1].Item2 && progress[^1].Item2 >= 1_000_000, "progress reaches the store's size");
        var moved = await StoreLocation.GetAsync(root);
        Check(moved is { Moved: true, Available: true } && Path.GetFullPath(moved.Folder) == Path.GetFullPath(dest), "moved");
        Check(MainWindowViewModel.TrtOnDisk(), "TensorRT is read through the default path");

        Check((await StoreLocation.ChangeAsync(root, ["reset"], null))?.Contains("move it back instead") == true, "reset is refused while the folder is there");

        Directory.Move(dest, dest + "-unplugged");
        await vm.RefreshStorage();
        Check(vm.StorageMissing && !MainWindowViewModel.TrtOnDisk(), "unplugged");
        Check(vm.AnimeJaNaiConf.TensorRtSelected && vm.BackendNotice.Contains("isn't available"), "unplugged: the backend stays, notice: " + vm.BackendNotice);
        Check((await StoreLocation.ChangeAsync(root, ["move", "--default"], null))?.Contains("missing") == true, "move back is refused while missing");
        Check(await StoreLocation.ChangeAsync(root, ["reset"], null) is null, "reset");
        Check(await StoreLocation.GetAsync(root) == new StoreLocation.Info(storeRoot, false, null, true), "a default store again");
        Check(Directory.Exists(dest + "-unplugged"), "reset never touches the folder");

        Directory.Move(dest + "-unplugged", dest);
        Check(await StoreLocation.ChangeAsync(root, ["move", Path.Combine(dir, "d2")], null) is null, "move to a new folder");
        Check(await StoreLocation.ChangeAsync(root, ["move", "--default"], null) is null, "move back");
        Check(await StoreLocation.GetAsync(root) is { Moved: false }, "back at the default path");
        Console.WriteLine("PASS move, unplug, reset and move back through aji-runtime");
    }

    Console.WriteLine(skipped == 0 ? "storage tests passed" : $"storage tests passed ({skipped} skipped)");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
finally
{
    StoreLocation.AjiRuntimeOverride = null;
    Environment.SetEnvironmentVariable("AJI_RUNTIME_STORE", oldStore);
    // a failed step can leave the store's default path a link (maybe a dangling one): remove the link
    // itself first, so cleaning up never follows it
    try
    {
        var link = new DirectoryInfo(Path.Combine(dir, "store"));
        if (link.LinkTarget is not null)
        {
            link.Delete();
        }
        Directory.Delete(dir, true);
    }
    catch (IOException) { }
    catch (UnauthorizedAccessException) { }
}
