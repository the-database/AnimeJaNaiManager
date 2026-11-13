// Components > Storage: reading the updater's `--storage --json` (and `aji-runtime location --json`
// when the updater is older), where a move or a chosen folder goes, when the section shows, which
// option is current and which buttons it offers, switching through the updater, and that a store
// whose folder is missing doesn't switch the backend to DirectML (while a copy with nothing installed
// yet isn't taken for one). The updater and aji-runtime are played by this exe (FakeTools.cs). With
// AJI_RUNTIME_EXE (an aji-runtime 0.2.0+ build) it also drives the real tool on a store in a
// temporary folder. Exits nonzero on failure.
using System.Text.Json;
using AnimeJaNaiConfEditor.Services;
using AnimeJaNaiConfEditor.ViewModels;
using ReactiveUI.Builder;

if (FakeTools.Run(args) is int fakeExitCode)
{
    return fakeExitCode;
}

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

    // --storage --json
    {
        var inside = StoreLocation.ParseChoice("""
            {"choice": "inside", "root": "D:\\mpv\\animejanai\\runtime", "moved": false, "target": null, "available": true,
             "inside": "D:\\mpv\\animejanai\\runtime", "shared": "C:\\Users\\me\\AppData\\Local\\the-database\\aji", "installed": true}
            """)!;
        Check(inside is { Choice: StoreLocation.StoreChoice.Inside, Installed: true, Available: true, IsShared: false }, "inside");
        Check(inside.Folder == @"D:\mpv\animejanai\runtime" && inside.Inside == inside.Root && inside.SharedFolder == @"C:\Users\me\AppData\Local\the-database\aji", "inside: its folders");
        Check(inside.Text == "", "inside: nothing to say");
        var shared = StoreLocation.ParseChoice("""{"choice": "shared", "root": "C:\\aji", "moved": true, "target": "F:\\aji", "available": true, "inside": "D:\\in", "shared": "C:\\aji", "installed": true}""")!;
        Check(shared is { Choice: StoreLocation.StoreChoice.Shared, IsShared: true, MovedHere: true, Missing: false } && shared.Folder == @"F:\aji" && shared.SharedFolder == @"F:\aji", "shared, moved");
        Check(shared.Text.Contains(@"Moved from C:\aji"), "shared, moved: " + shared.Text);
        // the shared folder was moved, but this copy keeps its runtime inside
        var insideMoved = StoreLocation.ParseChoice("""{"choice": "inside", "root": "D:\\in", "moved": true, "target": "F:\\aji", "available": true, "inside": "D:\\in", "shared": "C:\\aji", "installed": true}""")!;
        Check(insideMoved.Folder == @"D:\in" && !insideMoved.MovedHere && insideMoved.SharedFolder == @"F:\aji", "inside, the shared folder moved");
        var custom = StoreLocation.ParseChoice("""{"choice": "custom", "root": "G:\\JaNai", "available": false, "installed": false}""")!;
        Check(custom is { Choice: StoreLocation.StoreChoice.Custom, Missing: true, Moved: false, Inside: null } && custom.Text.Contains("isn't available"), "custom, unplugged");
        var fresh = StoreLocation.ParseChoice("""{"choice": "shared", "root": "C:\\aji", "available": true, "installed": false}""")!;
        Check(fresh.Text.Contains("isn't installed yet"), "nothing installed yet: " + fresh.Text);
        // anything else is an updater without the choice
        Check(StoreLocation.ParseChoice("""{"root": "C:\\a", "moved": false, "target": null, "available": true}""") is null, "aji-runtime's location is no choice");
        Check(StoreLocation.ParseChoice("""{"choice": "elsewhere", "root": "C:\\a"}""") is null, "an unknown choice");
        Check(StoreLocation.ParseChoice("""{"choice": "inside"}""") is null && StoreLocation.ParseChoice("""{"choice": "inside", "root": ""}""") is null, "no folder");
        Check(StoreLocation.ParseChoice("Usage: AnimeJaNaiUpdater.exe [--check|--apply]") is null && StoreLocation.ParseChoice("") is null
            && StoreLocation.ParseChoice("[1]") is null && StoreLocation.ParseChoice("\"inside\"") is null, "garbage");
        // aji-runtime's answer is the shared store (no choice)
        var old = StoreLocation.Parse("""{"root": "C:\\a", "moved": false, "target": null, "available": true}""")!;
        Check(old is { Choice: null, IsShared: true } && old.SharedFolder == @"C:\a" && old.Text.Contains("default folder"), "an older updater: the shared store");
        Console.WriteLine("PASS --storage --json is read");
    }

    // A folder I choose: a store as it is, a new or empty folder, else a folder of its own inside
    {
        string pick = Path.Combine(dir, "pick");
        Check(StoreLocation.ChoiceTarget(pick) == pick, "a new folder");
        Directory.CreateDirectory(pick);
        Check(StoreLocation.ChoiceTarget(pick) == pick, "an empty folder");
        File.WriteAllText(Path.Combine(pick, "holiday.mkv"), "x");
        string own = Path.Combine(pick, "JaNai shared runtime");
        Check(StoreLocation.ChoiceTarget(pick) == own, "a folder with files");
        Directory.CreateDirectory(own);
        File.WriteAllText(Path.Combine(own, "store.json"), """{"format": 1}""");
        Check(StoreLocation.ChoiceTarget(pick) == own && StoreLocation.IsStore(own), "the folder another app made there");
        Check(StoreLocation.ChoiceTarget(own) == own, "a store picked itself");
        string store = Path.Combine(dir, "other-app-store");
        Directory.CreateDirectory(Path.Combine(store, "packs"));
        File.WriteAllText(Path.Combine(store, "store.json"), """{"format": 1}""");
        Check(StoreLocation.ChoiceTarget(store) == store, "another app's store, with files, is shared as it is");

        var info = new StoreLocation.Info(@"C:\aji", false, null, true) { Choice = StoreLocation.StoreChoice.Custom, Inside = @"D:\mpv\animejanai\runtime", Shared = @"C:\aji" };
        Check(StoreLocation.ChoiceArgument(info, @"D:\mpv\animejanai\runtime") == "inside", "the inside folder is inside");
        Check(!OperatingSystem.IsWindows() || StoreLocation.ChoiceArgument(info, @"d:\MPV\animejanai\runtime\") == "inside", "however it is written");
        Check(StoreLocation.ChoiceArgument(info, @"C:\aji") == "shared", "the shared folder is shared");
        Check(StoreLocation.ChoiceArgument(info, own) == own, "anything else is a folder");
        Check(StoreLocation.SameFolder(@"C:\a\", @"c:\A") == OperatingSystem.IsWindows() && !StoreLocation.SameFolder(@"C:\a", null), "same folder");
        Console.WriteLine("PASS where a chosen folder goes");
    }

    // shown where a store is used and TensorRT is, or can be, installed
    {
        var info = new StoreLocation.Info(@"C:\aji", false, null, true);
        Check(!StoreLocation.Shows(null, true, true), "no store (a dev build, a Manager without the updater)");
        Check(StoreLocation.Shows(info, true, false), "TensorRT installed");
        Check(StoreLocation.Shows(info, false, true) && StoreLocation.Shows(info, false, null), "an NVIDIA GPU (or not known yet), nothing installed");
        Check(!StoreLocation.Shows(info, false, false), "no NVIDIA GPU that can run TensorRT, and no TensorRT");
        Console.WriteLine("PASS when the section shows");
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

    // From here this exe plays the updater and aji-runtime (FakeTools.cs).
    string fakeDir = Path.Combine(dir, "fake");
    Directory.CreateDirectory(fakeDir);
    Environment.SetEnvironmentVariable("AJM_FAKE_DIR", fakeDir);
    string self = Environment.ProcessPath!;
    string insideDir = Path.Combine(data, "runtime");
    string sharedDir = Path.Combine(dir, "shared");
    string ajiRuntimeJson = Path.Combine(inference, "aji-runtime.json");
    string ajiRuntimeJsonText = File.ReadAllText(ajiRuntimeJson);
    void Fake(string mode) => File.WriteAllText(Path.Combine(fakeDir, "mode.txt"), mode);
    void State(string choice, string storeRoot, bool installed, bool available = true, bool moved = false, string? target = null) =>
        File.WriteAllText(Path.Combine(fakeDir, "storage.json"), JsonSerializer.Serialize(new
        {
            choice, root = storeRoot, moved, target, available, inside = insideDir, shared = sharedDir, installed,
        }));
    string[] Calls() => File.Exists(Path.Combine(fakeDir, "calls.txt")) ? File.ReadAllLines(Path.Combine(fakeDir, "calls.txt")) : [];
    string LastCall() => Calls() is { Length: > 0 } c ? c[^1] : "";
    string Call(params string[] a) => root + "\u001e" + string.Join("\u001f", a);
    File.WriteAllText(Path.Combine(fakeDir, "location.json"),
        JsonSerializer.Serialize(new { root = sharedDir, moved = false, target = (string?)null, available = true }));
    StoreLocation.AjiRuntimeOverride = self;
    StoreLocation.UpdaterOverride = self;

    // what's read: the updater's choice, else (an older updater) aji-runtime's shared store, if TensorRT is in it
    {
        Fake("ok");
        State("inside", insideDir, installed: false);
        var read = await StoreLocation.ReadAsync(root, data);
        Check(read is { Choice: StoreLocation.StoreChoice.Inside, Installed: false } && read.Root == insideDir && read.Shared == sharedDir, "the choice");
        Check(LastCall() == Call("--storage", "--json"), "run in the install root with --storage --json: " + LastCall());

        foreach (var mode in new[] { "old", "garbage" })
        {
            Fake(mode);
            File.Delete(ajiRuntimeJson);
            Check(await StoreLocation.ReadAsync(root, data) is null, mode + ": nothing installed in the shared store, nothing to show (as before)");
            File.WriteAllText(ajiRuntimeJson, ajiRuntimeJsonText);
            Check(await StoreLocation.ReadAsync(root, data) == new StoreLocation.Info(sharedDir, false, null, true), mode + ": the shared store, from aji-runtime");
            Check(LastCall().EndsWith("\u001elocation\u001f--json"), mode + ": aji-runtime location --json: " + LastCall());
        }
        StoreLocation.AjiRuntimeOverride = null;
        Fake("ok");
        Check(await StoreLocation.ReadAsync(root, data) is null, "no aji-runtime at the install root: nothing to show");
        StoreLocation.AjiRuntimeOverride = self;
        Console.WriteLine("PASS what Storage reads");
    }

    // A fresh copy (nothing installed) whose chosen folder's drive isn't there: not "TensorRT only looks missing".
    {
        File.Delete(ajiRuntimeJson);
        State("custom", @"F:\nowhere", installed: false, available: false);
        await vm.RefreshStorage();
        Check(vm.ShowStorage && vm.StorageIsCustom && vm.StorageUnavailable && vm.StorageCustomMissing && !vm.StorageMissing, "fresh, unplugged: shown, not missing TensorRT");
        Check(!vm.BackendNotice.Contains("isn't available"), "fresh: notice " + vm.BackendNotice);
        if (OperatingSystem.IsWindows())
        {
            Check(vm.AnimeJaNaiConf.DirectMlSelected, "fresh: TensorRT really is missing, DirectML");
            vm.AnimeJaNaiConf.BackendAutoFallback = false;
            vm.AnimeJaNaiConf.SetTensorRtSelected();
        }
        // and with nothing in the way, nothing is unavailable
        State("inside", insideDir, installed: false);
        await vm.RefreshStorage();
        Check(vm.ShowStorage && vm.StorageIsInside && !vm.StorageUnavailable && vm.StorageText.Contains("isn't installed yet"), "fresh: inside, nothing installed yet");
        if (OperatingSystem.IsWindows())
        {
            vm.AnimeJaNaiConf.BackendAutoFallback = false;
            vm.AnimeJaNaiConf.SetTensorRtSelected();
        }
        File.WriteAllText(ajiRuntimeJson, ajiRuntimeJsonText);
        Console.WriteLine("PASS a fresh copy isn't a missing folder");
    }

    // Installed in a chosen folder whose drive is unplugged: the backend stays, the notice and banner say why.
    {
        State("custom", @"F:\aji2", installed: true, available: false);
        await vm.RefreshStorage();
        Check(vm.StorageMissing && vm.StorageCustomMissing && !vm.StorageSharedMissing && vm.AnimeJaNaiConf.TensorRtSelected, "custom, unplugged: TensorRT stays");
        Check(vm.BackendNotice.Contains(@"F:\aji2 isn't available"), "notice: " + vm.BackendNotice);
        Check(vm.StorageMissingBanner.Contains(@"F:\aji2") && vm.StorageMissingBanner.Contains("choose another place") && !vm.ShowSetupNeeded, "banner: " + vm.StorageMissingBanner);
        Check(vm.StorageSummary == "Folder not available" && vm.StorageSectionExpanded, "the section opens on it");
        Console.WriteLine("PASS a chosen folder that is missing keeps the backend");
    }

    // which option is current, and the shared folder's buttons only with it
    {
        State("inside", insideDir, installed: true, moved: true, target: @"G:\moved");
        await vm.RefreshStorage();
        Check(vm.HasStorageChoice && vm.StorageIsInside && !vm.StorageIsShared && !vm.StorageIsCustom, "inside is current");
        Check(vm.StorageInsideFolder == insideDir && vm.StorageSharedFolder == @"G:\moved" && vm.StorageFolderText == insideDir, "inside: the folders");
        Check(!vm.AllowSharedStorageChanges && vm.AllowStorageChanges && !vm.StorageMoved && vm.StorageSummary == "Inside this install", "inside: no Move... / Move Back");
        Check(vm.StorageCustomFolder == "No folder chosen", "inside: no folder of my own");

        State("shared", sharedDir, installed: true, moved: true, target: @"G:\moved");
        await vm.RefreshStorage();
        Check(vm.StorageIsShared && vm.AllowSharedStorageChanges && vm.StorageMoved && vm.StorageFolderText == @"G:\moved", "shared, moved: Move..., Move Back to Default");
        State("shared", sharedDir, installed: true, moved: true, target: @"G:\moved", available: false);
        await vm.RefreshStorage();
        Check(vm.StorageSharedMissing && !vm.StorageCustomMissing && vm.StorageMissingBanner.Contains("reset the location"), "shared, missing: Try Again, Reset to Default");

        string mine = Path.Combine(dir, "my runtime ランタイム");
        State("custom", mine, installed: true);
        await vm.RefreshStorage();
        Check(vm.StorageIsCustom && vm.StorageCustomFolder == mine && !vm.AllowSharedStorageChanges && vm.StorageSummary == mine, "custom is current (the folder read back in full)");

        // an updater without the choice: just the shared folder, as before
        Fake("old");
        await vm.RefreshStorage();
        Check(vm.ShowStorage && !vm.HasStorageChoice && vm.NoStorageChoice && vm.StorageIsShared && vm.AllowSharedStorageChanges, "older updater: the shared folder only");
        Fake("ok");
        Console.WriteLine("PASS which option is current");
    }

    // switching through the updater
    {
        State("inside", insideDir, installed: true);
        await vm.RefreshStorage();
        // a folder with a space, other scripts and a trailing separator reaches the updater as one argument
        string mine = Path.Combine(dir, "my runtime ランタイム") + Path.DirectorySeparatorChar;
        var progress = new List<(long, long)>();
        Check(await StoreLocation.SwitchAsync(root, mine, (d, t) => progress.Add((d, t))) is null, "switch");
        Check(LastCall() == Call("--storage", mine), "--storage <folder>: " + LastCall());
        Check(progress.Count == 2 && progress[^1] == (2_000_000, 2_000_000), "progress from AJI_MOVE");

        Check(await vm.SwitchStorage("shared"), "switch to shared");
        // the switch, then storage is read again
        Check(Calls()[^2] == Call("--storage", "shared") && LastCall() == Call("--storage", "--json"), "--storage shared, then --storage --json");
        Check(vm.StorageIsShared && vm.StorageFolderText == sharedDir, $"now shared: {vm.Storage}");
        Check(vm.StorageStatusText == "Done." && vm.AllowStorageChanges && !vm.StorageBusy && !vm.ComponentManager.IsBusy, "done, unblocked");

        Fake("fail");
        Check(!await vm.SwitchStorage("inside"), "a failed switch");
        Check(vm.StorageStatusText == "The player is still running. Close it and try again.", "the updater's reason: " + vm.StorageStatusText);
        Check(vm.StorageIsShared && vm.AllowStorageChanges && !vm.ComponentManager.IsBusy, "still shared, unblocked");
        Fake("ok");

        int calls = Calls().Length;
        vm.ComponentManager.IsBusy = true; // a component install
        Check(!await vm.SwitchStorage("inside") && Calls().Length == calls, "no switch while components change");
        vm.ComponentManager.IsBusy = false;
        vm.StorageBusy = true; // another storage change
        Check(!await vm.SwitchStorage("inside") && Calls().Length == calls, "no switch while another storage change runs");
        vm.StorageBusy = false;
        Check(await vm.SwitchStorage("inside") && vm.StorageIsInside, "then it switches");
        Console.WriteLine("PASS switching through the updater");
    }

    // back to the state the integration test below expects: no updater, aji-runtime.json in place
    StoreLocation.UpdaterOverride = null;
    StoreLocation.AjiRuntimeOverride = fakeTool;
    Environment.SetEnvironmentVariable("AJM_FAKE_DIR", null);
    File.WriteAllText(ajiRuntimeJson, ajiRuntimeJsonText);
    vm.AnimeJaNaiConf.BackendAutoFallback = false;
    vm.AnimeJaNaiConf.SetTensorRtSelected();

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
    StoreLocation.UpdaterOverride = null;
    Environment.SetEnvironmentVariable("AJM_FAKE_DIR", null);
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
