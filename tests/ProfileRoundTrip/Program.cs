using AnimeJaNaiConfEditor.ViewModels;
using ReactiveUI.Builder;

try
{
    RxAppBuilder.CreateReactiveUIBuilder().WithCoreServices().BuildApp();
    string data = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(data, "onnx"));
    Environment.SetEnvironmentVariable("ANIMEJANAI_DATA_DIR", data);
    File.WriteAllText(Path.Combine(data, "animejanai.conf"),
        "[global]\nconfig_version=2\nbackend=DirectML\ndefault_slot=0\n" +
        "[slot_1]\nprofile_name=Round trip\nchain_1_rife=yes\nchain_1_rife_ensemble=yes\n");
    var vm = new MainWindowViewModel();
    vm.SelectedSlotNumber = "1";
    if (vm.AnimeJaNaiConf.AutoDisplayRefresh)
        throw new Exception("Automatic display refresh must default to off");
    foreach (bool enabled in new[] { true, false })
    {
        vm.AnimeJaNaiConf.AutoDisplayRefresh = enabled;
        string full = Path.Combine(data, $"display-refresh-{enabled}.conf");
        vm.WriteAnimeJaNaiConf(full);
        if (vm.ReadAnimeJaNaiConf(full).AutoDisplayRefresh != enabled)
            throw new Exception($"Full config lost automatic display refresh={enabled}");
        if (vm.ReadAnimeJaNaiConf(Path.Combine(data, "animejanai.conf")).AutoDisplayRefresh != enabled)
            throw new Exception($"Autosave lost automatic display refresh={enabled}");
        Console.WriteLine($"PASS automatic display refresh={enabled} export/reload and autosave");
    }
    // GPU choice: gpu_tensorrt / gpu_directml, written only when a GPU is chosen.
    {
        string Text(string conf) => File.ReadAllText(conf).Replace("\r", "");
        string auto = Path.Combine(data, "gpu-auto.conf");
        vm.WriteAnimeJaNaiConf(auto);
        if (Text(auto).Contains("gpu_"))
            throw new Exception("Auto must not write gpu_ keys");
        vm.AnimeJaNaiConf.GpuTensorRt = "5a57ccea-430c-db57-6b2e-a4e89f017fe0";
        vm.AnimeJaNaiConf.GpuDirectMl = "Intel(R) UHD Graphics 770";
        string chosen = Path.Combine(data, "gpu-chosen.conf");
        vm.WriteAnimeJaNaiConf(chosen);
        var reread = vm.ReadAnimeJaNaiConf(chosen);
        if (reread.GpuTensorRt != "5a57ccea-430c-db57-6b2e-a4e89f017fe0" || reread.GpuDirectMl != "Intel(R) UHD Graphics 770" ||
            !Text(chosen).Contains("gpu_directml=Intel(R) UHD Graphics 770\n"))
            throw new Exception($"Full config lost the GPU choice:\n{Text(chosen)}");
        vm.AnimeJaNaiConf.GpuTensorRt = "";
        vm.AnimeJaNaiConf.GpuDirectMl = "";
        string autoAgain = Path.Combine(data, "gpu-auto-again.conf");
        vm.WriteAnimeJaNaiConf(autoAgain);
        if (Text(autoAgain) != Text(auto))
            throw new Exception("Going back to Auto must restore the original config");
        Console.WriteLine("PASS GPU choice round-trips, Auto writes nothing");
    }

    foreach (bool ensemble in new[] { true, false })
    {
        vm.CurrentSlot.Chains[0].RifeEnsemble = ensemble;
        string profile = Path.Combine(data, $"ensemble-{ensemble}.conf");
        vm.WriteAnimeJaNaiCurrentProfileConf(profile);
        // Change the destination first so a skipped assignment cannot pass.
        vm.CurrentSlot.Chains[0].RifeEnsemble = !ensemble;
        vm.ReadAnimeJaNaiConfToCurrentSlot(profile, false);
        if (vm.CurrentSlot.Chains[0].RifeEnsemble != ensemble)
            throw new Exception($"Profile import lost RIFE ensemble={ensemble}");
        Console.WriteLine($"PASS ensemble={ensemble}");
    }


    // Temporal fix: chain_N_temporal_fix_model / _scene_threshold, the models untouched.
    foreach (var name in new[] { "temporalfix_s1_v1_op18_fp16", "temporalfix_s1_v1.1_op18_fp16",
                                 "temporalfix_s2_v1_op18_fp16", "temporalfix_s3_v1_op18_fp16", "upscaler" })
        File.WriteAllBytes(Path.Combine(data, "onnx", name + ".onnx"), []);
    if (MainWindowViewModel.TemporalFixModelName(1) != "temporalfix_s1_v1.1_op18_fp16")
        throw new Exception("Temporal fix must pick the newest model version, not the last in ordinal order");
    var chain = vm.CurrentSlot.Chains[0];
    chain.AddModel();
    chain.Models[0].Name = "upscaler";
    string Section(string conf) => File.ReadAllText(conf).Replace("\r", "");
    string off = Path.Combine(data, "tfix-off.conf");
    vm.WriteAnimeJaNaiConf(off);
    if (Section(off).Contains("temporal_fix") || Section(off).Contains("chain_1_upscale") || Section(off).Contains("model_2_"))
        throw new Exception("Features that are off must not write keys");
    foreach (int strength in new[] { 1, 2, 3 })
    {
        chain.EnableTemporalFix = true;
        chain.TemporalFixStrength = strength;
        chain.TemporalFixSceneThreshold = 0.2M;
        string full = Path.Combine(data, $"tfix-{strength}.conf");
        vm.WriteAnimeJaNaiConf(full);
        string text = Section(full);
        if (!text.Contains($"chain_1_temporal_fix_model={MainWindowViewModel.TemporalFixModelName(strength)}\n") ||
            !text.Contains("chain_1_temporal_fix_scene_threshold=0.2\n") || text.Contains("model_2_") ||
            !text.Contains("chain_1_model_1_name=upscaler\n"))
            throw new Exception($"Full config lacks the temporal fix keys for strength {strength}:\n{text}");
        var reread = vm.ReadAnimeJaNaiConf(full).UpscaleSlots.First(s => s.SlotNumber == "1").Chains[0];
        if (!reread.EnableTemporalFix || reread.TemporalFixStrength != strength || reread.TemporalFixSceneThreshold != 0.2M ||
            reread.Models.Count != 1 || reread.Models[0].Name != "upscaler")
            throw new Exception($"Full config lost temporal fix strength {strength}");

        string profile = Path.Combine(data, $"tfix-profile-{strength}.conf");
        vm.WriteAnimeJaNaiCurrentProfileConf(profile);
        chain.EnableTemporalFix = false;
        vm.ReadAnimeJaNaiConfToCurrentSlot(profile, false);
        chain = vm.CurrentSlot.Chains[0];
        if (!chain.EnableTemporalFix || chain.TemporalFixStrength != strength || chain.Models.Count != 1)
            throw new Exception($"Profile import lost temporal fix strength {strength}");
        Console.WriteLine($"PASS temporal fix strength={strength}");
    }
    // Order: temporal_fix_before_upscale is written either way while temporal fix is on.
    foreach (bool before in new[] { true, false })
    {
        chain.TemporalFixBeforeUpscale = before;
        string full = Path.Combine(data, $"tfix-before-{before}.conf");
        vm.WriteAnimeJaNaiConf(full);
        if (!Section(full).Contains($"chain_1_temporal_fix_before_upscale={(before ? "yes" : "no")}\n"))
            throw new Exception($"Full config lacks temporal_fix_before_upscale={before}");
        if (vm.ReadAnimeJaNaiConf(full).UpscaleSlots.First(s => s.SlotNumber == "1").Chains[0].TemporalFixBeforeUpscale != before)
            throw new Exception($"Full config lost temporal fix before upscale={before}");
        string profile = Path.Combine(data, $"tfix-before-profile-{before}.conf");
        vm.WriteAnimeJaNaiCurrentProfileConf(profile);
        chain.TemporalFixBeforeUpscale = !before;
        vm.ReadAnimeJaNaiConfToCurrentSlot(profile, false);
        chain = vm.CurrentSlot.Chains[0];
        if (chain.TemporalFixBeforeUpscale != before)
            throw new Exception($"Profile import lost temporal fix before upscale={before}");
        Console.WriteLine($"PASS temporal fix before upscale={before}");
    }
    chain.TemporalFixBeforeUpscale = UpscaleChain.DefaultTemporalFixBeforeUpscale;

    chain.EnableTemporalFix = false;
    string offAgain = Path.Combine(data, "tfix-off-again.conf");
    vm.WriteAnimeJaNaiConf(offAgain);
    if (Section(offAgain) != Section(off))
        throw new Exception("Turning temporal fix off must restore the original config");
    Console.WriteLine("PASS temporal fix off restores the config");

    // Upscaling off: chain_N_upscale=no, the models stay where they are.
    chain.EnableUpscale = false;
    chain.EnableTemporalFix = true;
    chain.TemporalFixStrength = 2;
    string noUpscale = Path.Combine(data, "no-upscale.conf");
    vm.WriteAnimeJaNaiConf(noUpscale);
    string noUpscaleText = Section(noUpscale);
    if (!noUpscaleText.Contains("chain_1_upscale=no\n") || !noUpscaleText.Contains("chain_1_model_1_name=upscaler\n") ||
        !noUpscaleText.Contains($"chain_1_temporal_fix_model={MainWindowViewModel.TemporalFixModelName(2)}\n"))
        throw new Exception($"Upscaling off must write upscale=no and keep the models:\n{noUpscaleText}");
    var rereadOff = vm.ReadAnimeJaNaiConf(noUpscale).UpscaleSlots.First(s => s.SlotNumber == "1").Chains[0];
    if (rereadOff.EnableUpscale || !rereadOff.EnableTemporalFix || rereadOff.Models.Count != 1 || rereadOff.Models[0].Name != "upscaler")
        throw new Exception("Full config lost upscaling off");
    string noUpscaleProfile = Path.Combine(data, "no-upscale-profile.conf");
    vm.WriteAnimeJaNaiCurrentProfileConf(noUpscaleProfile);
    vm.ReadAnimeJaNaiConfToCurrentSlot(noUpscaleProfile, false);
    chain = vm.CurrentSlot.Chains[0];
    if (chain.EnableUpscale || chain.Models.Count != 1 || chain.Models[0].Name != "upscaler")
        throw new Exception("Profile import lost upscaling off");
    chain.EnableUpscale = true;
    chain.EnableTemporalFix = false;
    string upscaleAgain = Path.Combine(data, "upscale-again.conf");
    vm.WriteAnimeJaNaiConf(upscaleAgain);
    if (Section(upscaleAgain) != Section(off))
        throw new Exception("Turning upscaling back on must restore the original config");
    Console.WriteLine("PASS upscaling off keeps the models and round-trips");

    // A temporalfix model in the model list (written by hand) is just a model.
    string hand = Path.Combine(data, "tfix-hand.conf");
    File.WriteAllText(hand,
        "[global]\nconfig_version=3\n" +
        "[slot_1]\nprofile_name=Trailing\nchain_1_model_1_name=upscaler\nchain_1_model_2_name=temporalfix_s3_v1_op18_fp16\n");
    var trailing = vm.ReadAnimeJaNaiConf(hand).UpscaleSlots.First(s => s.SlotNumber == "1").Chains[0];
    if (trailing.EnableTemporalFix || trailing.Models.Count != 2 || !trailing.Models[1].AllModels.Contains("temporalfix_s3_v1_op18_fp16"))
        throw new Exception("A temporalfix model in the model list must stay a normal, selectable model");
    if (trailing.Models[0].AllModels.Any(m => m.StartsWith("temporalfix")))
        throw new Exception("Temporal fix models must not be offered in the model list");
    Console.WriteLine("PASS hand-written temporalfix model stays a model");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
