using AnimeJaNaiConfEditor.Services;
using Avalonia.Collections;
using ReactiveUI;
using Salaros.Configuration;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using ReactiveUI.Primitives;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.ViewModels
{
    [DataContract]
    public class MainWindowViewModel : ViewModelBase
    {
        private static readonly CultureInfo ENGLISH_CULTURE = CultureInfo.GetCultureInfo("en-US");

        public static MainWindowViewModel? Instance { get; private set; }

        public MainWindowViewModel()
        {
            Instance = this;
            UiState.Load(DataDir);
            AnimeJaNaiConf = ReadAnimeJaNaiConf(AnimeJaNaiConfPath, true);
            // Built after the user conf loads so the read-only default profiles reflect the saved
            // standard/sharp preset.
            RefreshDefaultProfiles();


            for (var i = 0; i < AnimeJaNaiConf.UpscaleSlots.Count; i++)
            {
                AnimeJaNaiConf.UpscaleSlots[i].MpvProfileName = $"upscale-on-{i + 1}";
            }

            CheckAndDoBackup();

            SelectedMpvProfile = ReadCurrentProfileFromMpvConf();

            InitializeSelectedSlot();

            RefreshComponentAwareness();
            ComponentManager.Refreshed += RefreshComponentAwareness;
            this.WhenAnyValue(x => x.AnimeJaNaiConf.DirectMlSelected).Subscribe(_ => RaiseGpuChoice());
            _ = LoadDxgiGpusAsync();
            _ = InitializeComponentManagerAsync();
        }

        // ---- component awareness for the Profiles tab -------------------------------------
        // Install state comes from the disk (works offline); GPU identity comes from the
        // component engine once its refresh completes.

        // The TensorRT core library: nvinfer_11.dll on Windows, libnvinfer.so.11 on Linux.
        public static bool TrtOnDisk() =>
            File.Exists(Path.Combine(DataDir, "inference",
                OperatingSystem.IsWindows() ? "nvinfer_11.dll" : "libnvinfer.so.11"));

        // DirectML (D3D12) is Windows-only; on Linux TensorRT is the only backend.
        public bool DirectMlAvailable => OperatingSystem.IsWindows();

        public static bool RifeOnDisk() =>
            Directory.Exists(Path.Combine(DataDir, "rife")) &&
            Directory.EnumerateFiles(Path.Combine(DataDir, "rife"), "*.onnx").Any();

        // Temporal fix (vs_temporalfix) model files, temporalfix_s<strength>_v<version>_*.onnx.
        public static readonly Regex TemporalFixModelRegex = new(@"^temporalfix_s(\d+)_v(\d+(?:\.\d+)*)_", RegexOptions.IgnoreCase);
        public static readonly List<int> TemporalFixStrengths = [1, 2, 3];
        // The files shipped in onnx/; used when the folder has no model for a strength.
        private static readonly Dictionary<int, string> BundledTemporalFixModels = new()
        {
            [1] = "temporalfix_s1_v1.1_op18_fp16",
            [2] = "temporalfix_s2_v1_op18_fp16",
            [3] = "temporalfix_s3_v1_op18_fp16",
        };

        // The newest temporalfix model for a strength in onnx/ (no extension), or null.
        public static string? TemporalFixModelOnDisk(int strength)
        {
            var onnx = Path.Combine(DataDir, "onnx");
            if (!Directory.Exists(onnx))
                return null;
            return Directory.EnumerateFiles(onnx, $"temporalfix_s{strength}_v*.onnx")
                .Select(Path.GetFileNameWithoutExtension)
                .Select(name => (name, m: TemporalFixModelRegex.Match(name!)))
                .Where(x => x.m.Success && int.Parse(x.m.Groups[1].Value) == strength)
                .OrderBy(x => Version.Parse(x.m.Groups[2].Value.Contains('.') ? x.m.Groups[2].Value : x.m.Groups[2].Value + ".0"))
                .Select(x => x.name)
                .LastOrDefault();
        }

        public static string TemporalFixModelName(int strength) =>
            TemporalFixModelOnDisk(strength) ?? BundledTemporalFixModels.GetValueOrDefault(strength, $"temporalfix_s{strength}_v1_op18_fp16");

        public static bool TemporalFixOnDisk() =>
            TemporalFixStrengths.All(s => TemporalFixModelOnDisk(s) != null);

        private bool _trtSelectable = true;
        public bool TrtSelectable
        {
            get => _trtSelectable;
            set => this.RaiseAndSetIfChanged(ref _trtSelectable, value);
        }

        private string _backendNotice = "";
        public string BackendNotice
        {
            get => _backendNotice;
            set
            {
                this.RaiseAndSetIfChanged(ref _backendNotice, value);
                this.RaisePropertyChanged(nameof(HasBackendNotice));
            }
        }

        public bool HasBackendNotice => !string.IsNullOrEmpty(BackendNotice);

        private bool _rifeMissing;
        public bool RifeMissing
        {
            get => _rifeMissing;
            set => this.RaiseAndSetIfChanged(ref _rifeMissing, value);
        }

        private bool _temporalFixMissing;
        public bool TemporalFixMissing
        {
            get => _temporalFixMissing;
            set => this.RaiseAndSetIfChanged(ref _temporalFixMissing, value);
        }

        public void RefreshComponentAwareness()
        {
            bool trtInstalled = TrtOnDisk();
            bool? nvidia = ComponentManager.GpuNvidia;
            bool trtUsable = trtInstalled && nvidia != false;
            TrtSelectable = trtUsable;

            string notice = "";
            if (!DirectMlAvailable)
            {
                // Linux has no DirectML: TensorRT is the only backend, installed or not
                TrtSelectable = true;
                if (AnimeJaNaiConf?.DirectMlSelected == true)
                {
                    AnimeJaNaiConf.BackendAutoFallback = false;
                    AnimeJaNaiConf.SetTensorRtSelected();
                }
                if (!trtInstalled)
                    notice = "TensorRT is not installed - install it from the Components tab.";
            }
            else if (trtUsable && AnimeJaNaiConf != null && AnimeJaNaiConf.DirectMlSelected &&
                AnimeJaNaiConf.BackendAutoFallback)
            {
                // TensorRT is the natural path on NVIDIA: the DirectML selection was
                // only our fallback, so installing TensorRT completes the setup
                AnimeJaNaiConf.BackendAutoFallback = false;
                AnimeJaNaiConf.SetTensorRtSelected();
                notice = "TensorRT is installed — the backend has switched back to TensorRT.";
            }
            else if (!trtUsable)
            {
                // a conf pointing at an unusable backend would only fail at playback;
                // flip it to the engine that ships in every install and say so
                string flipped = "";
                if (AnimeJaNaiConf != null && AnimeJaNaiConf.TensorRtSelected)
                {
                    AnimeJaNaiConf.BackendAutoFallback = true;
                    AnimeJaNaiConf.SetDirectMlSelected();
                    flipped = "Switched to DirectML: ";
                }
                notice = nvidia == false
                    ? flipped + "TensorRT requires an NVIDIA GPU."
                    : ComponentManager.TrtPackAvailable
                        ? flipped + "TensorRT is not installed — install it from the Components tab (recommended for NVIDIA GPUs); the backend switches back automatically once installed."
                        : flipped + "TensorRT is not installed.";
            }
            BackendNotice = notice;
            RifeMissing = !RifeOnDisk();
            TemporalFixMissing = !TemporalFixOnDisk();

            // per-chain RIFE and temporal fix toggles derive from the Missing flags; let them re-evaluate
            foreach (var slot in AnimeJaNaiConf?.UpscaleSlots ?? [])
            {
                foreach (var chain in slot.Chains)
                {
                    chain.RaisePropertyChanged(nameof(UpscaleChain.RifeToggleEnabled));
                    chain.RaisePropertyChanged(nameof(UpscaleChain.TemporalFixToggleEnabled));
                }
            }

            // the NVIDIA GPU list arrives with a component refresh
            RaiseGpuChoice();
        }

        // ---- GPU choice -------------------------------------------------------------------
        // Which GPU upscales, per backend (AnimeJaNaiConf.GpuTensorRt / GpuDirectMl). The same
        // safeguards as VideoJaNai: graphics cards first, integrated graphics labelled and last,
        // a warning when one is chosen over a graphics card, and no claim about which card is
        // fastest. The row is always there when the backend has a GPU, so the settings say which
        // GPU upscales; it can only be changed with more than one.

        // DXGI's adapters, enumerated once in the background (Direct3D 12 device creation per
        // adapter takes a moment); null until then.
        private List<DxgiGpu>? _dxgiGpus;

        private async Task LoadDxgiGpusAsync()
        {
            _dxgiGpus = await Task.Run(GpuAdapters.Enumerate);
            RaiseGpuChoice();
        }

        private sealed record GpuEntry(string Label, string Key, bool? Integrated);

        private bool DirectMlChoice => AnimeJaNaiConf?.DirectMlSelected == true;

        // The GPUs the selected backend can upscale on. TensorRT: the NVIDIA GPUs, in NVML's
        // order, keyed by device UUID (two of the same model are numbered). DirectML: every
        // hardware adapter, keyed by name, in Windows' high-performance order with integrated
        // graphics moved last.
        private List<GpuEntry> BackendGpus()
        {
            if (DirectMlChoice)
            {
                return (_dxgiGpus ?? [])
                    .Select(g => new GpuEntry(g.Name, g.Name, g.Integrated))
                    .OrderBy(g => g.Integrated == true)
                    .ToList();
            }
            var nvidia = ComponentManager.NvidiaGpus;
            return nvidia
                .Select((g, i) => new GpuEntry(
                    nvidia.Count(o => o.Name == g.Name) > 1
                        ? $"{g.Name} ({nvidia.Take(i + 1).Count(o => o.Name == g.Name)})"
                        : g.Name,
                    g.Uuid, false))
                .ToList();
        }

        private string ChosenGpuKey
        {
            get => (DirectMlChoice ? AnimeJaNaiConf?.GpuDirectMl : AnimeJaNaiConf?.GpuTensorRt) ?? "";
            set
            {
                if (AnimeJaNaiConf == null)
                {
                    return;
                }
                if (DirectMlChoice)
                {
                    AnimeJaNaiConf.GpuDirectMl = value;
                }
                else
                {
                    AnimeJaNaiConf.GpuTensorRt = value;
                }
            }
        }

        // The chosen GPU when this PC has it, else null (Auto, or a GPU no longer here: the
        // player then uses its default GPU too).
        private GpuEntry? ChosenGpu(List<GpuEntry> gpus)
        {
            var key = ChosenGpuKey;
            return key.Length == 0 ? null
                : gpus.FirstOrDefault(g => string.Equals(g.Key, key, StringComparison.OrdinalIgnoreCase));
        }

        // The GPU Auto means, where that's known: on DirectML the GPU Windows ranks first for high
        // performance (the player's d3d11-adapter-preference), on TensorRT the only NVIDIA GPU.
        private GpuEntry? AutoGpu(List<GpuEntry> gpus)
        {
            if (DirectMlChoice)
            {
                return _dxgiGpus is { Count: > 0 } all
                    ? gpus.FirstOrDefault(g => g.Key == all[0].Name)
                    : null;
            }
            return gpus.Count == 1 ? gpus[0] : null;
        }

        public bool GpuChoiceVisible => BackendGpus().Count > 0;
        public bool GpuChoiceEnabled => BackendGpus().Count > 1;

        public bool GpuChoiceIntegratedWarning
        {
            get
            {
                var gpus = BackendGpus();
                return ChosenGpu(gpus)?.Integrated == true && gpus.Any(g => g.Integrated == false);
            }
        }

        // Auto, then each GPU. The same array while the labels are the same: the list clears its
        // selection when its items are replaced, and would write that back.
        private string[] _gpuChoices = [];
        public string[] GpuChoices
        {
            get
            {
                var gpus = BackendGpus();
                string auto = AutoGpu(gpus) is GpuEntry a ? $"Auto ({a.Label})" : "Auto";
                string[] choices = [auto, .. gpus.Select(g => g.Integrated == true ? $"{g.Label} (integrated)" : g.Label)];
                if (!choices.SequenceEqual(_gpuChoices))
                {
                    _gpuChoices = choices;
                }
                return _gpuChoices;
            }
        }

        // True while SelectedGpuChoiceIndex reports -1 to put the list's selection back.
        private bool _gpuChoiceResync;

        // True while RaiseGpuChoice replaces the list's items: the list then writes back an index
        // of its own (e.g. 0 after a backend change), which isn't the user's choice.
        private bool _gpuChoiceRaising;

        public int SelectedGpuChoiceIndex
        {
            get
            {
                if (_gpuChoiceResync)
                {
                    return -1;
                }
                var gpus = BackendGpus();
                return ChosenGpu(gpus) is GpuEntry g ? gpus.IndexOf(g) + 1 : 0;
            }
            set
            {
                // The list clears its selection (-1) when its items are replaced (a GPU list
                // arriving, a backend change). Raising the same index again doesn't restore it, so
                // once the list has settled report -1 and then the index.
                if (value < 0 || _gpuChoiceRaising)
                {
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _gpuChoiceResync = true;
                        this.RaisePropertyChanged(nameof(SelectedGpuChoiceIndex));
                        _gpuChoiceResync = false;
                        this.RaisePropertyChanged(nameof(SelectedGpuChoiceIndex));
                    });
                    return;
                }
                var gpus = BackendGpus();
                if (value == 0)
                {
                    // Auto clears the choice, unless the list just shows Auto because the chosen
                    // GPU isn't (or isn't yet) in it
                    if (ChosenGpu(gpus) != null)
                    {
                        ChosenGpuKey = "";
                    }
                }
                else if (value <= gpus.Count)
                {
                    ChosenGpuKey = gpus[value - 1].Key;
                }
                this.RaisePropertyChanged(nameof(GpuChoiceIntegratedWarning));
            }
        }

        private void RaiseGpuChoice()
        {
            _gpuChoiceRaising = true;
            try
            {
                this.RaisePropertyChanged(nameof(GpuChoices));
                this.RaisePropertyChanged(nameof(SelectedGpuChoiceIndex));
            }
            finally
            {
                _gpuChoiceRaising = false;
            }
            this.RaisePropertyChanged(nameof(GpuChoiceVisible));
            this.RaisePropertyChanged(nameof(GpuChoiceEnabled));
            this.RaisePropertyChanged(nameof(GpuChoiceIntegratedWarning));
        }

        public ComponentManagerViewModel ComponentManager { get; } = new();

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set => this.RaiseAndSetIfChanged(ref _selectedTabIndex, value);
        }

        // First-run setup: if the hardware's recommended components are missing
        // (fresh slim install, or a GPU change), open on the Components tab and
        // offer to install everything in one click - new users won't read the
        // tab, and 3.3.0 set the bar by shipping preconfigured.
        private async Task InitializeComponentManagerAsync()
        {
            await ComponentManager.RefreshAsync();
            if (!ComponentManager.SetupNeeded)
            {
                return;
            }
            SelectedTabIndex = 1;

            var missing = ComponentManager.MissingPreselected;
            var lines = string.Join("\n",
                missing.Select(p => $"\u2022  {p.Title} \u2014 {p.SizeText}"));
            long totalMb = missing.Sum(p => p.Bytes) / 1048576;
            var dialog = new FluentAvalonia.UI.Controls.FAContentDialog
            {
                Title = "Set up AnimeJaNai",
                Content = $"Components recommended for this PC are not installed:\n\n{lines}\n\nDownload and install them now ({totalMb:N0} MB)?",
                PrimaryButtonText = "Install",
                CloseButtonText = "Not now",
                DefaultButton = FluentAvalonia.UI.Controls.FAContentDialogButton.Primary,
            };
            try
            {
                // Avalonia 12: give the dialog the window explicitly; the implicit host
                // lookup can land on an internal root that FluentAvalonia can't use
                var window = (Avalonia.Application.Current?.ApplicationLifetime as
                    Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.MainWindow;
                var result = window != null ? await dialog.ShowAsync(window) : await dialog.ShowAsync();
                if (result == FluentAvalonia.UI.Controls.FAContentDialogResult.Primary)
                {
                    await ComponentManager.InstallMissingPreselectedAsync();
                }
            }
            catch
            {
                // no dialog host (window closing etc.) - the Components tab with
                // its banner and highlights remains the manual path
            }
        }

        // Rebuilds the read-only default profiles from DEFAULT_PROFILES_CONF, swapping each profile's
        // HD models to their V3.1Sharp1 variants per its own preset. Standard and sharp HD model
        // filenames differ only by the _HD_V3.1_ vs _HD_V3.1Sharp1_ token (the SD model has none).
        public void RefreshDefaultProfiles()
        {
            DefaultUpscaleSlots = ReadAnimeJaNaiConf(new ConfigParser(DEFAULT_PROFILES_CONF)).UpscaleSlots;
            DefaultUpscaleSlots[0].MpvProfileName = "upscale-on-quality";
            DefaultUpscaleSlots[1].MpvProfileName = "upscale-on-balanced";
            DefaultUpscaleSlots[2].MpvProfileName = "upscale-on-performance";
            DefaultUpscaleSlots[0].DescriptionText = "Minimum Suggested GPU: NVIDIA RTX 4090";
            DefaultUpscaleSlots[1].DescriptionText = "Minimum Suggested GPU: NVIDIA RTX 3080";
            DefaultUpscaleSlots[2].DescriptionText = "Minimum Suggested GPU: NVIDIA RTX 3060";
            if (AnimeJaNaiConf != null)
            {
                ApplySharp(DefaultUpscaleSlots[0], AnimeJaNaiConf.QualitySharp);
                ApplySharp(DefaultUpscaleSlots[1], AnimeJaNaiConf.BalancedSharp);
                ApplySharp(DefaultUpscaleSlots[2], AnimeJaNaiConf.PerformanceSharp);
            }
            this.RaisePropertyChanged(nameof(CurrentSlot));
            this.RaisePropertyChanged(nameof(CurrentDefaultStandardSelected));
            this.RaisePropertyChanged(nameof(CurrentDefaultSharpSelected));
        }

        private static void ApplySharp(UpscaleSlot slot, bool sharp)
        {
            if (!sharp)
            {
                return;
            }
            foreach (var chain in slot.Chains)
            {
                foreach (var model in chain.Models)
                {
                    if (model.Name != null && model.Name.Contains("_HD_V3.1_"))
                    {
                        model.Name = model.Name.Replace("_HD_V3.1_", "_HD_V3.1Sharp1_");
                    }
                }
            }
        }

        // The Standard/Sharp toggle in the default-profiles view applies to whichever default profile
        // is currently shown (CurrentSlot). These map slot 1/2/3 -> Quality/Balanced/Performance.
        private bool CurrentDefaultSharpValue() => CurrentSlot?.SlotNumber switch
        {
            "1" => AnimeJaNaiConf?.QualitySharp ?? false,
            "2" => AnimeJaNaiConf?.BalancedSharp ?? false,
            "3" => AnimeJaNaiConf?.PerformanceSharp ?? false,
            _ => false,
        };

        public bool CurrentDefaultSharpSelected => CurrentDefaultSharpValue();
        public bool CurrentDefaultStandardSelected => !CurrentDefaultSharpValue();

        public void SetCurrentDefaultStandard() => SetCurrentDefaultPreset(false);
        public void SetCurrentDefaultSharp() => SetCurrentDefaultPreset(true);

        private void SetCurrentDefaultPreset(bool sharp)
        {
            switch (CurrentSlot?.SlotNumber)
            {
                case "1": AnimeJaNaiConf.QualitySharp = sharp; break;
                case "2": AnimeJaNaiConf.BalancedSharp = sharp; break;
                case "3": AnimeJaNaiConf.PerformanceSharp = sharp; break;
                default: return;
            }
            RefreshDefaultProfiles(); // also raises the toggle props
        }

        private string[] _commonResolutions = [
            "0x0",
            "640x360",
            "640x480",
            "720x480",
            "768x576",
            "960x540",
            "1024x576",
            "1280x720",
            "1440x1080",
            "1920x1080"];

        public string[] CommonResolutions
        {
            get => _commonResolutions;
            set => this.RaiseAndSetIfChanged(ref _commonResolutions, value);
        }

        public string[] BuilderOptimizationLevels { get; } = ["0", "1", "2", "3", "4", "5"];

        private static readonly string DEFAULT_PROFILES_CONF = @"[slot_1]
profile_name=Quality
chain_1_min_resolution=1280x720
chain_1_max_resolution=1920x1080
chain_1_min_fps=0
chain_1_max_fps=31
chain_1_model_1_resize_height_before_upscale=0
chain_1_model_1_resize_factor_before_upscale=100
chain_1_model_1_name=2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16
chain_1_rife=no
chain_2_min_resolution=0x0
chain_2_max_resolution=1280x720
chain_2_min_fps=0
chain_2_max_fps=31
chain_2_model_1_resize_height_before_upscale=0
chain_2_model_1_resize_factor_before_upscale=100
chain_2_model_1_name=2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op21_dynamo
chain_2_rife=no
chain_3_min_resolution=0x0
chain_3_max_resolution=1920x1080
chain_3_min_fps=0
chain_3_max_fps=61
chain_3_model_1_resize_height_before_upscale=0
chain_3_model_1_resize_factor_before_upscale=100
chain_3_model_1_name=2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16
chain_3_rife=no
[slot_2]
profile_name=Balanced
chain_1_min_resolution=1280x720
chain_1_max_resolution=1920x1080
chain_1_min_fps=0
chain_1_max_fps=31
chain_1_model_1_resize_height_before_upscale=0
chain_1_model_1_resize_factor_before_upscale=100
chain_1_model_1_name=2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16
chain_1_rife=no
chain_2_min_resolution=0x0
chain_2_max_resolution=1280x720
chain_2_min_fps=0
chain_2_max_fps=31
chain_2_model_1_resize_height_before_upscale=0
chain_2_model_1_resize_factor_before_upscale=100
chain_2_model_1_name=2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op21_dynamo
chain_2_rife=no
[slot_3]
profile_name=Performance
chain_1_min_resolution=1280x720
chain_1_max_resolution=1920x1080
chain_1_min_fps=0
chain_1_max_fps=31
chain_1_model_1_resize_height_before_upscale=0
chain_1_model_1_resize_factor_before_upscale=100
chain_1_model_1_name=2x_AnimeJaNai_HD_V3.1_Performance_SPANF3_b5f48_unshuffle_fp16
chain_1_rife=no
chain_2_min_resolution=0x0
chain_2_max_resolution=1280x720
chain_2_min_fps=0
chain_2_max_fps=31
chain_2_model_1_resize_height_before_upscale=0
chain_2_model_1_resize_factor_before_upscale=100
chain_2_model_1_name=2x_AnimeJaNai_SD_V1beta34_Compact_1x3xHxW_dyn-HW_strong_fp16_op21_dynamo
chain_2_rife=no";

        public string ExePath = Path.GetDirectoryName(Process.GetCurrentProcess().MainModule.FileName);

        // Two layouts: at the install root the exe sits next to mpvnet.exe with the data in
        // animejanai/ (3.4.0+); in the legacy layout (and `dotnet run` from the project
        // output, which copies animejanai.conf + onnx/ beside the binary) the exe lives
        // inside the data directory itself.
        public static bool AtInstallRoot { get; } =
            !File.Exists(Path.Combine(AppContext.BaseDirectory, "animejanai.conf")) &&
            File.Exists(Path.Combine(AppContext.BaseDirectory, "animejanai", "animejanai.conf"));

        // animejanai/: conf, onnx models, rife models, benchmarks, backups.
        // ANIMEJANAI_DATA_DIR / ANIMEJANAI_ROOT override the exe-relative layout
        // for the AppImage, where the binary is in a read-only mount but the
        // writable data lives under $XDG_DATA_HOME/AnimeJaNai.
        public static string DataDir { get; } = Path.GetFullPath(
            Environment.GetEnvironmentVariable("ANIMEJANAI_DATA_DIR") is { Length: > 0 } dd ? dd
            : AtInstallRoot ? Path.Combine(AppContext.BaseDirectory, "animejanai")
            : AppContext.BaseDirectory);

        // install root: player, AnimeJaNaiUpdater, portable_config/
        public static string RootDir { get; } = Path.GetFullPath(
            Environment.GetEnvironmentVariable("ANIMEJANAI_ROOT") is { Length: > 0 } rd ? rd
            : AtInstallRoot ? AppContext.BaseDirectory
            : Path.Combine(AppContext.BaseDirectory, ".."));

        public string BackupPath => Path.Combine(DataDir, "backups");

        public string MpvConfPath => Path.Combine(RootDir, "portable_config", "mpv.conf");

        public string AnimeJaNaiConfPath => Path.Combine(DataDir, "animejanai.conf");

        public string OnnxPath => Path.Combine(DataDir, "onnx");

        private bool _showGlobalSettings = false; // TODO
        [DataMember]
        public bool ShowGlobalSettings
        {
            get => _showGlobalSettings;
            set => this.RaiseAndSetIfChanged(ref _showGlobalSettings, value);
        }

        private bool _showDefaultProfiles;
        [DataMember]
        public bool ShowDefaultProfiles
        {
            get => _showDefaultProfiles;
            set
            {
                this.RaiseAndSetIfChanged(ref _showDefaultProfiles, value);
                this.RaisePropertyChanged(nameof(CurrentSlot));
            }
        }

        private bool _showCustomProfiles = true; // TODO 
        [DataMember]
        public bool ShowCustomProfiles
        {
            get => _showCustomProfiles;
            set
            {
                this.RaiseAndSetIfChanged(ref _showCustomProfiles, value);
                this.RaisePropertyChanged(nameof(CurrentSlot));
            }
        }

        private string _selectedSlotNumber = "1"; // TODO
        [DataMember]
        public string SelectedSlotNumber
        {
            get => _selectedSlotNumber;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedSlotNumber, value);
                this.RaisePropertyChanged(nameof(CurrentSlot));
                this.RaisePropertyChanged(nameof(CurrentDefaultStandardSelected));
                this.RaisePropertyChanged(nameof(CurrentDefaultSharpSelected));
            }
        }

        private string? _selectedMpvProfile;
        [DataMember]
        public string? SelectedMpvProfile
        {
            get => _selectedMpvProfile;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedMpvProfile, value);
                this.RaisePropertyChanged(nameof(MpvConfDetected));
            }
        }

        public bool MpvConfDetected => File.Exists(MpvConfPath);


        // Global Settings: the advanced TensorRT engine block starts collapsed
        public bool TrtSectionExpanded
        {
            get => UiState.Current.GetExpanded("global:trt") ?? false;
            set
            {
                UiState.Current.SetExpanded("global:trt", value);
                this.RaisePropertyChanged();
            }
        }

        public double SidebarWidth
        {
            get => UiState.Current.SidebarWidth;
            set => UiState.Current.SetSidebarWidth(value);
        }

        public void HandleShowGlobalSettings()
        {
            ShowGlobalSettings = true;
            ShowCustomProfiles = false;
            ShowDefaultProfiles = false;
        }

        public void HandleShowDefaultProfile(string slotNumber)
        {
            ShowGlobalSettings = false;
            ShowDefaultProfiles = true;
            ShowCustomProfiles = false;
            SelectedSlotNumber = slotNumber;
        }

        public void HandleShowCustomProfile(string slotNumber)
        {
            ShowGlobalSettings = false;
            ShowCustomProfiles = true;
            ShowDefaultProfiles = false;
            SelectedSlotNumber = slotNumber;
        }

        private AnimeJaNaiConf _animeJaNaiConf;
        public AnimeJaNaiConf AnimeJaNaiConf
        {
            get => _animeJaNaiConf;
            set => this.RaiseAndSetIfChanged(ref _animeJaNaiConf, value);
        }

        public UpscaleSlot CurrentSlot
        {
            get => ShowCustomProfiles ? AnimeJaNaiConf.UpscaleSlots.Where(slot => slot.SlotNumber == SelectedSlotNumber).FirstOrDefault() :
                ShowDefaultProfiles ? DefaultUpscaleSlots.Where(slot => slot.SlotNumber == SelectedSlotNumber).FirstOrDefault() : null;
        }

        private UpscaleSlot? _selectedProfileToClone;
        public UpscaleSlot? SelectedProfileToClone
        {
            get => _selectedProfileToClone;
            set
            {
                this.RaiseAndSetIfChanged(ref _selectedProfileToClone, value);
                this.RaisePropertyChanged(nameof(CanCloneProfile));
            }
        }

        public bool CanCloneProfile => true; //SelectedProfileToClone != null;

        public void AddChain()
        {
            CurrentSlot.Chains.Add(new UpscaleChain(true) { Vm = this });
            UpdateChainHeaders();
        }

        public void DeleteChain(UpscaleChain chain)
        {
            try
            {
                CurrentSlot.Chains.Remove(chain);
            }
            catch (ArgumentOutOfRangeException)
            {

            }

            UpdateChainHeaders();
        }

        public void UpdateChainHeaders()
        {
            for (var i = 0; i < CurrentSlot.Chains.Count; i++)
            {
                CurrentSlot.Chains[i].ChainNumber = (i + 1).ToString();
            }
        }

        private string _validationText = string.Empty;
        public string ValidationText
        {
            get => _validationText;
            set
            {
                this.RaiseAndSetIfChanged(ref _validationText, value);
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
        }

        private AvaloniaList<UpscaleSlot> _defaultUpscaleSlots = [];
        [DataMember]
        public AvaloniaList<UpscaleSlot> DefaultUpscaleSlots
        {
            get => _defaultUpscaleSlots;
            set
            {
                this.RaiseAndSetIfChanged(ref _defaultUpscaleSlots, value);
                this.RaisePropertyChanged(nameof(AllSlots));
            }
        }

        public AvaloniaList<UpscaleSlot> AllSlots => new(DefaultUpscaleSlots.Concat(AnimeJaNaiConf?.UpscaleSlots?.Where(x => x.ShowSlot && !x.ActiveSlot)));

        private bool _showAdvancedSettings = false;
        [DataMember]
        public bool ShowAdvancedSettings
        {
            get => _showAdvancedSettings;
            set => this.RaiseAndSetIfChanged(ref _showAdvancedSettings, value);
        }

        private bool _showConsole = false;
        public bool ShowConsole
        {
            get => _showConsole;
            set => this.RaiseAndSetIfChanged(ref _showConsole, value);
        }

        private string _inputStatusText = string.Empty;
        public string InputStatusText
        {
            get => _inputStatusText;
            set
            {
                this.RaiseAndSetIfChanged(ref _inputStatusText, value);
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
        }

        public string LeftStatus => !Valid ? ValidationText.Replace("\n", " ") : $"{InputStatusText} selected for upscaling.";

        private bool _valid = false;
        [IgnoreDataMember]
        public bool Valid
        {
            get => _valid;
            set
            {
                this.RaiseAndSetIfChanged(ref _valid, value);
                this.RaisePropertyChanged(nameof(LeftStatus));
            }
        }

        // Temporal fix models are left out (the chain's toggle adds them), except `current`, so a
        // hand-written conf that runs one mid-chain still shows its model.
        public AvaloniaList<string> GetAllModels(string? current = null)
        {
            return new AvaloniaList<string>(Directory.GetFiles(OnnxPath).Where(filename => Path.GetExtension(filename).Equals(".onnx", StringComparison.CurrentCultureIgnoreCase))
                .Select(filename => Path.GetFileNameWithoutExtension(filename))
                .Where(name => name == current || !TemporalFixModelRegex.IsMatch(name))
                .Order().ToList());
        }


        public void Validate()
        {
            Console.WriteLine("OK");
        }

        private static List<string>? _rifeModels = null;

        public static List<string> RifeModels
        {
            get
            {
                if (_rifeModels == null)
                {
                    var models = new List<string>();
                    var modelsPath = Path.Combine(DataDir, "rife");

                    if (!Directory.Exists(modelsPath))
                    {
                        return [];
                    }

                    var files = Directory.GetFiles(modelsPath, searchPattern: "*.onnx");

                    foreach (var file in files)
                    {
                        Debug.WriteLine(file);
                        var m = Regex.Match(Path.GetFileName(file), @"rife_v(\d+)\.(\d+)(_lite)?(_ensemble)?.onnx");
                        if (m.Success)
                        {
                            var model = m.Groups[1].Value + m.Groups[2].Value;
                            if (file.Contains("_lite"))
                            {
                                model += "1";
                            }

                            if (!models.Contains(model))
                            {
                                models.Add(model);
                            }
                        }
                    }

                    models.Sort(delegate (string m1, string m2)
                    {
                        var m1i = decimal.Parse(m1[..Math.Min(3, m1.Length)]);
                        var m2i = decimal.Parse(m2[..Math.Min(3, m2.Length)]);

                        if (m1.Length > 3)
                        {
                            m1i += .1m;
                        }

                        if (m2.Length > 3)
                        {
                            m2i += .1m;
                        }

                        return m2i.CompareTo(m1i);
                    });

                    _rifeModels = [.. models.Select(m => RifeValueToLabel(m))];
                }

                return _rifeModels;
            }
        }

        public static string RifeLabelToValue(string rifeLabel)
        {
            if (string.IsNullOrEmpty(rifeLabel))
            {
                return rifeLabel;
            }

            var m = Regex.Match(rifeLabel, @"RIFE (\d+)\.(\d+)( Lite)?");
            if (m.Success)
            {
                var value = $"{m.Groups[1].Value}{m.Groups[2].Value}";
                if (m.Groups[3].Success)
                {
                    value += "1";
                }
                return value;
            }

            throw new ArgumentException(rifeLabel);
        }

        public static string RifeValueToLabel(string rifeValue)
        {
            string dec;

            if (string.IsNullOrEmpty(rifeValue))
            {
                return rifeValue;
            }

            if (rifeValue.Length == 2)
            {
                dec = rifeValue[1].ToString();
            }
            else
            {
                dec = rifeValue.Substring(1, 2);
            }

            var modelName = $"RIFE {rifeValue[0]}.{dec}";

            if (rifeValue.Length >= 4 && rifeValue.EndsWith('1'))
            {
                modelName += " Lite";
            }

            return modelName;
        }

        public AnimeJaNaiConf ReadAnimeJaNaiConf(string fullPath, bool autoSave = false)
        {
            return ReadAnimeJaNaiConf(new ConfigParser(fullPath), autoSave);
        }

        // Keep CONFIG_VERSION, the current default, and the historical-defaults set in sync with
        // aji_conf.cpp in the animejanai-inference repo (the native filter's conf loader).
        private const int CONFIG_VERSION = 3;
        // [global] sub_render_mode value that selects the experimental GPU subtitle path. The
        // settings themselves live in the [subs-gpu] profile of the managed mpv-animejanai.conf,
        // so they can change with the player without a Manager release.
        private const string SUB_RENDER_MODE_GPU = "gpu";
        // TRT 11: strongly-typed is the default (--stronglyTyped is a no-op), and the native filter's
        // sanitizer strips --inputIOFormats/--outputIOFormats/--tacticSources anyway (types come from
        // the network; the cuDNN/cuBLAS tactic sources are gone). Only the builder optimization level,
        // build shape, and skip-inference are worth carrying.
        private const string DEFAULT_TRT_ENGINE_SETTINGS =
            "--builderOptimizationLevel=5 --optShapes=input:%video_resolution% --skipInference";
        private static readonly HashSet<string> LEGACY_DEFAULT_TRT_ENGINE_SETTINGS = new()
        {
            DEFAULT_TRT_ENGINE_SETTINGS,
            // pre-trim default (TRT 8/10-era flags); drop it from old confs so the current default applies
            "--stronglyTyped --optShapes=input:%video_resolution% --inputIOFormats=fp16:chw --outputIOFormats=fp16:chw --builderOptimizationLevel=5 --tacticSources=-CUDNN,-CUBLAS,-CUBLAS_LT --skipInference",
        };

        // Hand-edited confs can leave numeric keys empty; the native filter reads
        // empty as 0, but a non-numeric string must never reach a NumberBox binding
        // (InvalidCastException converting '' to double).
        private static string NumericOr(ConfigParser parser, string section, string key, string fallback = "0")
        {
            var value = parser.GetValue(section, key);
            return string.IsNullOrWhiteSpace(value) ||
                   !double.TryParse(value, System.Globalization.NumberStyles.Float, ENGLISH_CULTURE, out _)
                ? fallback : value;
        }

        public AnimeJaNaiConf ReadAnimeJaNaiConf(ConfigParser parser, bool autoSave = false)
        {
            var animeJaNaiConf = new AnimeJaNaiConf(autoSave) { Vm = this };
            var slots = new Dictionary<string, UpscaleSlot>();

            animeJaNaiConf.EnableLogging = ParseBool(parser.GetValue("global", "logging", "no"));
            animeJaNaiConf.AutoDisplayRefresh = ParseBool(parser.GetValue("global", "display_rate_match", "no"));
            animeJaNaiConf.BackendAutoFallback = ParseBool(parser.GetValue("global", "backend_auto_fallback", "no"));
            if (Enum.TryParse(parser.GetValue("global", "backend", "TensorRT"), out Backend backend))
            {
                switch (backend)
                {
                    case Backend.DirectML:
                    case Backend.NCNN: // retired; the inference shim treats NCNN as DirectML
                        animeJaNaiConf.SetDirectMlSelected();
                        break;
                    case Backend.TensorRT:
                    default:
                        animeJaNaiConf.SetTensorRtSelected();
                        break;
                }
            }

            if (int.TryParse(parser.GetValue("global", "default_slot", ""), out var defaultSlot))
            {
                animeJaNaiConf.DefaultSlot = defaultSlot;
            }

            // Subtitle rendering mode: "gpu" opts into the experimental GPU subtitle path
            // (the [subs-gpu] profile in mpv-animejanai.conf, applied at startup by
            // scripts/animejanai_backend.lua). Anything else (including the absent key)
            // means the stable defaults baked into the [animejanai] profile.
            animeJaNaiConf.GpuSubtitles = SUB_RENDER_MODE_GPU.Equals(
                parser.GetValue("global", "sub_render_mode", "").Trim(),
                StringComparison.OrdinalIgnoreCase);

            animeJaNaiConf.GpuTensorRt = parser.GetValue("global", "gpu_tensorrt", "").Trim();
            animeJaNaiConf.GpuDirectMl = parser.GetValue("global", "gpu_directml", "").Trim();

            // config_version drives migrations (absent => 1, the pre-versioning schema).
            int.TryParse(parser.GetValue("global", "config_version", "1"), out var configVersion);
            var trtEngineSettings = parser.GetValue("global", "trt_engine_settings", "");
            // v1 -> v2: drop a trt_engine_settings value that is just an old shipped default so the
            // current default applies; a genuinely customized value is kept.
            if (configVersion < 2 && LEGACY_DEFAULT_TRT_ENGINE_SETTINGS.Contains(trtEngineSettings))
            {
                trtEngineSettings = "";
            }
            // v2 -> v3: TRT 11 strongly-typed ONNX dropped --fp16/--bf16 (the backend strips them and
            // the precision selector was removed); rewrite either to --stronglyTyped so a migrated
            // custom value stays valid, collapsing a duplicate if both were present.
            if (configVersion < 3 && Regex.IsMatch(trtEngineSettings, @"--(fp16|bf16)\b"))
            {
                trtEngineSettings = Regex.Replace(trtEngineSettings, @"--(fp16|bf16)\b", "--stronglyTyped");
                trtEngineSettings = Regex.Replace(trtEngineSettings, @"--stronglyTyped(\s+--stronglyTyped)+", "--stronglyTyped");
            }
            animeJaNaiConf.TrtEngineSettings = string.IsNullOrEmpty(trtEngineSettings)
                ? DEFAULT_TRT_ENGINE_SETTINGS
                : trtEngineSettings;

            // v2 (animejanai 3.3.0) had a single global default_preset for all default profiles; v3
            // split it into per-profile quality/balanced/performance keys. Use default_preset as the
            // fallback so a migrated conf keeps its Sharp selection. The per-profile keys (when
            // present in a v3 conf) take precedence; default_preset is absent in v3 confs.
            var legacyPreset = parser.GetValue("global", "default_preset", "standard");
            bool IsSharp(string key) => parser.GetValue("global", key, legacyPreset).Trim()
                .Equals("sharp", StringComparison.OrdinalIgnoreCase);
            animeJaNaiConf.QualitySharp = IsSharp("quality_preset");
            animeJaNaiConf.BalancedSharp = IsSharp("balanced_preset");
            animeJaNaiConf.PerformanceSharp = IsSharp("performance_preset");

            foreach (var section in parser.Sections)
            {
                if (section.SectionName != "global")
                {
                    var currentSlotNumber = Regex.Match(section.SectionName, @"slot_(\d+)").Groups[1].Value;

                    var chains = new Dictionary<string, UpscaleChain>();
                    var models = new Dictionary<string, Dictionary<string, UpscaleModel>>();

                    foreach (var key in section.Keys)
                    {
                        var matchCurrentChainNumber = Regex.Match(key.Name, @"chain_(\d+)_");

                        if (matchCurrentChainNumber.Success)
                        {
                            var currentChainNumber = matchCurrentChainNumber.Groups[1].Value;

                            chains[currentChainNumber] = new UpscaleChain(autoSave)
                            {
                                Vm = this,
                                ChainNumber = currentChainNumber,
                                MinResolution = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_min_resolution"),
                                MaxResolution = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_max_resolution"),
                                MinFps = NumericOr(parser, section.SectionName, $"chain_{currentChainNumber}_min_fps"),
                                MaxFps = NumericOr(parser, section.SectionName, $"chain_{currentChainNumber}_max_fps"),
                                EnableRife = ParseBool(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife")),
                                //RifeModel = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_model"),
                                RifeEnsemble = ParseBool(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_ensemble")),
                                RifeBeforeUpscale = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_before_upscale") is { } rbu ? ParseBool(rbu) : true,
                            };

                            if (int.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_factor_numerator"), ENGLISH_CULTURE, out var numerator))
                            {
                                chains[currentChainNumber].RifeFactorNumerator = numerator;
                            }

                            if (int.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_factor_denominator"), ENGLISH_CULTURE, out var denominator))
                            {
                                chains[currentChainNumber].RifeFactorDenominator = denominator;
                            }

                            if (decimal.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_scene_detect_threshold"), ENGLISH_CULTURE, out var scene_detect_threshold))
                            {
                                chains[currentChainNumber].RifeSceneDetectThreshold = scene_detect_threshold;
                            }

                            var rifeModel = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_model");

                            if (rifeModel != null)
                            {
                                chains[currentChainNumber].RifeModel = RifeValueToLabel(rifeModel);
                            }

                            ReadModelKey(parser, section.SectionName, key.Name, currentChainNumber, models, autoSave);
                        }
                    }

                    foreach (var (currentChainNumber, chain) in chains)
                    {
                        chain.Models = ReadChainFeatures(parser, section.SectionName, chain, models);
                    }

                    slots[currentSlotNumber] = new UpscaleSlot(autoSave)
                    {
                        Vm = this,
                        SlotNumber = currentSlotNumber,
                        ProfileName = parser.GetValue(section.SectionName, "profile_name", "New Profile"),
                        Chains = new AvaloniaList<UpscaleChain>(chains.Values.ToList())
                    };
                }
            }

            animeJaNaiConf.UpscaleSlots = new AvaloniaList<UpscaleSlot>(slots.Values);

            return animeJaNaiConf;
        }

        public void ReadAnimeJaNaiConfToCurrentSlot(string fullPath, bool autoSave = false)
        {
            ReadAnimeJaNaiConfToCurrentSlot(new ConfigParser(fullPath), autoSave);
        }

        public void ReadAnimeJaNaiConfToCurrentSlot(ConfigParser parser, bool autoSave = false)
        {
            var sectionKey = "slot";

            foreach (var section in parser.Sections)
            {
                if (section.SectionName != sectionKey)
                {
                    return; // Invalid
                }

                var currentSlotNumber = Regex.Match(section.SectionName, @"slot_(\d+)").Groups[1].Value;

                var chains = new Dictionary<string, UpscaleChain>();
                var models = new Dictionary<string, Dictionary<string, UpscaleModel>>();

                foreach (var key in section.Keys)
                {
                    var matchCurrentChainNumber = Regex.Match(key.Name, @"chain_(\d+)_");

                    if (matchCurrentChainNumber.Success)
                    {
                        var currentChainNumber = matchCurrentChainNumber.Groups[1].Value;

                        chains[currentChainNumber] = new UpscaleChain(autoSave)
                        {
                            Vm = this,
                            ChainNumber = currentChainNumber,
                            MinResolution = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_min_resolution"),
                            MaxResolution = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_max_resolution"),
                            MinFps = NumericOr(parser, section.SectionName, $"chain_{currentChainNumber}_min_fps"),
                            MaxFps = NumericOr(parser, section.SectionName, $"chain_{currentChainNumber}_max_fps"),
                            EnableRife = ParseBool(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife")),
                            RifeEnsemble = ParseBool(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_ensemble")),
                            RifeBeforeUpscale = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_before_upscale") is { } rbu ? ParseBool(rbu) : true,
                        };

                        if (int.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_factor_numerator"), ENGLISH_CULTURE, out var numerator))
                        {
                            chains[currentChainNumber].RifeFactorNumerator = numerator;
                        }

                        if (int.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_factor_denominator"), ENGLISH_CULTURE, out var denominator))
                        {
                            chains[currentChainNumber].RifeFactorDenominator = denominator;
                        }

                        if (decimal.TryParse(parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_scene_detect_threshold"), ENGLISH_CULTURE, out var scene_detect_threshold))
                        {
                            chains[currentChainNumber].RifeSceneDetectThreshold = scene_detect_threshold;
                        }

                        var rifeModel = parser.GetValue(section.SectionName, $"chain_{currentChainNumber}_rife_model");

                        if (rifeModel != null)
                        {
                            chains[currentChainNumber].RifeModel = RifeValueToLabel(rifeModel);
                        }

                        ReadModelKey(parser, section.SectionName, key.Name, currentChainNumber, models, true);
                    }
                }

                foreach (var (currentChainNumber, chain) in chains)
                {
                    chain.Models = ReadChainFeatures(parser, section.SectionName, chain, models);
                }

                CurrentSlot.Vm = this;
                CurrentSlot.ProfileName = parser.GetValue(sectionKey, "profile_name", "New Profile");
                CurrentSlot.Chains = new AvaloniaList<UpscaleChain>(chains.Values.ToList());
            }
        }

        static bool ParseBool(string value)
        {
            return value?.ToLower() == "yes";
        }

        // One chain_N_model_M_* key: reads that model once.
        private void ReadModelKey(ConfigParser parser, string section, string keyName, string chainNumber,
            Dictionary<string, Dictionary<string, UpscaleModel>> models, bool autoSave)
        {
            var match = Regex.Match(keyName, @"^chain_\d+_model_(\d+)_");
            if (!match.Success)
                return;
            var modelNumber = match.Groups[1].Value;
            if (!models.TryGetValue(chainNumber, out var chainModels))
                models[chainNumber] = chainModels = [];
            if (chainModels.ContainsKey(modelNumber))
                return;

            string Key(string field) => $"chain_{chainNumber}_model_{modelNumber}_{field}";
            var modelName = parser.GetValue(section, Key("name"), "0x0");
            chainModels[modelNumber] = new UpscaleModel(autoSave)
            {
                Vm = this,
                AllModels = GetAllModels(modelName),
                ModelNumber = modelNumber,
                Name = modelName,
                ResizeFactorBeforeUpscale = parser.GetValue(section, Key("resize_factor_before_upscale"), 100.ToString()),
                ResizeHeightBeforeUpscale = parser.GetValue(section, Key("resize_height_before_upscale"), 0.ToString()),
                Frames = parser.GetValue(section, Key("frames"), 0.ToString()),
                TemporalSceneThreshold = parser.GetValue(section, Key("temporal_scene_threshold"), UpscaleModel.DefaultTemporalSceneThreshold)
            };
        }

        // The chain's models plus its feature keys: chain_N_upscale=no (the engine skips the
        // models but they stay in the conf) and chain_N_temporal_fix_model (the engine runs it
        // after the models, or alone). Both need aji v0.17.0+; temporal_fix_before_upscale
        // (run it before the models instead) needs v0.17.1+.
        private static AvaloniaList<UpscaleModel> ReadChainFeatures(ConfigParser parser, string section, UpscaleChain chain,
            Dictionary<string, Dictionary<string, UpscaleModel>> models)
        {
            string? Get(string key) => parser.GetValue(section, $"chain_{chain.ChainNumber}_{key}");

            chain.EnableUpscale = Get("upscale") is not { } upscale || ParseBool(upscale);

            if (Get("temporal_fix_model") is { Length: > 0 } tfix)
            {
                var match = TemporalFixModelRegex.Match(tfix);
                if (match.Success && int.TryParse(match.Groups[1].Value, out var strength) && TemporalFixStrengths.Contains(strength))
                    chain.TemporalFixStrength = strength;
                if (decimal.TryParse(Get("temporal_fix_scene_threshold"), NumberStyles.Float, ENGLISH_CULTURE, out var threshold))
                    chain.TemporalFixSceneThreshold = threshold;
                // absent = the engine's default order, after the models
                chain.TemporalFixBeforeUpscale = Get("temporal_fix_before_upscale") is { } before && ParseBool(before);
                chain.EnableTemporalFix = true;
            }

            return new AvaloniaList<UpscaleModel>(models.TryGetValue(chain.ChainNumber, out var m) ? m.Values : []);
        }

        private static void WriteChainModels(ConfigParser parser, string section, UpscaleChain chain)
        {
            foreach (var model in chain.Models)
            {
                parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_resize_height_before_upscale", string.Create(ENGLISH_CULTURE, $"{model.ResizeHeightBeforeUpscale}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_resize_factor_before_upscale", string.Create(ENGLISH_CULTURE, $"{model.ResizeFactorBeforeUpscale}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_name", string.Create(ENGLISH_CULTURE, $"{model.Name}"));
                WriteTemporalKeys(parser, section, chain, model);
            }

            // write-minimal: these keys only appear while the feature differs from the default
            if (!chain.EnableUpscale)
                parser.SetValue(section, $"chain_{chain.ChainNumber}_upscale", "no");
            if (chain.EnableTemporalFix)
            {
                parser.SetValue(section, $"chain_{chain.ChainNumber}_temporal_fix_model", TemporalFixModelName(chain.TemporalFixStrength));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_temporal_fix_scene_threshold", string.Create(ENGLISH_CULTURE, $"{chain.TemporalFixSceneThreshold}"));
                // always written, so the Manager's default order can change without
                // changing what existing confs mean
                parser.SetValue(section, $"chain_{chain.ChainNumber}_temporal_fix_before_upscale", chain.TemporalFixBeforeUpscale ? "yes" : "no");
            }
        }


        public void WriteAnimeJaNaiConf()
        {
            WriteAnimeJaNaiConf(AnimeJaNaiConfPath);
        }

        public void WriteAnimeJaNaiConf(string fullPath)
        {
            var parser = ParsedAnimeJaNaiConf(AnimeJaNaiConf);
            parser?.Save(fullPath);
        }

        public static ConfigParser? ParsedAnimeJaNaiConf(AnimeJaNaiConf conf)
        {
            if (conf is null)
            {
                return null;
            }

            var parser = new ConfigParser();

            parser.SetValue("global", "config_version", CONFIG_VERSION.ToString());
            parser.SetValue("global", "backend", conf.SelectedBackend.ToString());
            parser.SetValue("global", "backend_auto_fallback", conf.BackendAutoFallback ? "yes" : "no");
            parser.SetValue("global", "logging", conf.EnableLogging ? "yes" : "no");
            // Write-minimal: only persist default_slot when the user picked one (absent => the vf
            // line's baked-in Balanced slot applies).
            if (conf.DefaultSlot is int defaultSlot)
            {
                parser.SetValue("global", "default_slot", defaultSlot.ToString(ENGLISH_CULTURE));
            }
            // Write-minimal: only persist the subtitle mode when the experimental GPU path is
            // enabled (absent => the stable [animejanai] subtitle defaults apply).
            if (conf.GpuSubtitles)
            {
                parser.SetValue("global", "sub_render_mode", SUB_RENDER_MODE_GPU);
            }
            if (conf.AutoDisplayRefresh)
            {
                parser.SetValue("global", "display_rate_match", "yes");
            }
            // Write-minimal: only a chosen GPU (absent => Auto).
            if (!string.IsNullOrWhiteSpace(conf.GpuTensorRt))
            {
                parser.SetValue("global", "gpu_tensorrt", conf.GpuTensorRt.Trim());
            }
            if (!string.IsNullOrWhiteSpace(conf.GpuDirectMl))
            {
                parser.SetValue("global", "gpu_directml", conf.GpuDirectMl.Trim());
            }
            // Write-minimal: only persist trt_engine_settings when it differs from the current
            // default, so future default changes apply automatically to users who didn't customize.
            if (conf.TrtEngineSettings != DEFAULT_TRT_ENGINE_SETTINGS)
            {
                parser.SetValue("global", "trt_engine_settings", conf.TrtEngineSettings);
            }
            // Write-minimal: only persist a profile's preset when sharp (absent => standard).
            if (conf.QualitySharp)
            {
                parser.SetValue("global", "quality_preset", "sharp");
            }
            if (conf.BalancedSharp)
            {
                parser.SetValue("global", "balanced_preset", "sharp");
            }
            if (conf.PerformanceSharp)
            {
                parser.SetValue("global", "performance_preset", "sharp");
            }

            foreach (var profile in conf.UpscaleSlots)
            {
                var section = $"slot_{profile.SlotNumber}";
                parser.SetValue(section, "profile_name", profile.ProfileName);

                foreach (var chain in profile.Chains)
                {
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_min_resolution", string.Create(ENGLISH_CULTURE, $"{chain.MinResolution}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_max_resolution", string.Create(ENGLISH_CULTURE, $"{chain.MaxResolution}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_min_fps", string.Create(ENGLISH_CULTURE, $"{chain.MinFps}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_max_fps", string.Create(ENGLISH_CULTURE, $"{chain.MaxFps}"));

                    WriteChainModels(parser, section, chain);

                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife", chain.EnableRife ? "yes" : "no");
                    chain.RifeModel ??= chain.RifeModelList.FirstOrDefault("");

                    var rifeModel = RifeLabelToValue(chain.RifeModel);

                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_model", rifeModel);
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_factor_numerator", string.Create(ENGLISH_CULTURE, $"{chain.RifeFactorNumerator ?? 1}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_factor_denominator", string.Create(ENGLISH_CULTURE, $"{chain.RifeFactorDenominator ?? 1}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_scene_detect_threshold", string.Create(ENGLISH_CULTURE, $"{chain.RifeSceneDetectThreshold ?? 0.015M}"));
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_ensemble", chain.RifeEnsemble ? "yes" : "no");
                    parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_before_upscale", chain.RifeBeforeUpscale ? "yes" : "no");
                }
            }

            return parser;
        }

        public void WriteAnimeJaNaiCurrentProfileConf(string fullPath)
        {
            var parser = ParsedAnimeJaNaiProfileConf(CurrentSlot);
            parser.Save(fullPath);
        }

        // Temporal model keys are optional: written only when they carry
        // something, so confs without temporal models stay unchanged.
        private static void WriteTemporalKeys(ConfigParser parser, string section, UpscaleChain chain, UpscaleModel model)
        {
            if (model.Frames != 0.ToString())
                parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_frames", string.Create(ENGLISH_CULTURE, $"{model.Frames}"));
            if (model.IsTemporal || model.TemporalSceneThreshold != UpscaleModel.DefaultTemporalSceneThreshold)
                parser.SetValue(section, $"chain_{chain.ChainNumber}_model_{model.ModelNumber}_temporal_scene_threshold", string.Create(ENGLISH_CULTURE, $"{model.TemporalSceneThreshold}"));
        }

        public ConfigParser ParsedAnimeJaNaiProfileConf(UpscaleSlot slot)
        {
            var parser = new ConfigParser();
            var section = "slot";

            parser.SetValue(section, "profile_name", slot.ProfileName);
            foreach (var chain in slot.Chains)
            {
                parser.SetValue(section, $"chain_{chain.ChainNumber}_min_resolution", string.Create(ENGLISH_CULTURE, $"{chain.MinResolution}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_max_resolution", string.Create(ENGLISH_CULTURE, $"{chain.MaxResolution}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_min_fps", string.Create(ENGLISH_CULTURE, $"{chain.MinFps}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_max_fps", string.Create(ENGLISH_CULTURE, $"{chain.MaxFps}"));

                WriteChainModels(parser, section, chain);

                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife", chain.EnableRife ? "yes" : "no");
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_factor_numerator", string.Create(ENGLISH_CULTURE, $"{chain.RifeFactorNumerator ?? 0}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_factor_denominator", string.Create(ENGLISH_CULTURE, $"{chain.RifeFactorDenominator ?? 1}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_model", string.Create(ENGLISH_CULTURE, $"{RifeLabelToValue(chain.RifeModel)}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_ensemble", chain.RifeEnsemble ? "yes" : "no");
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_scene_detect_threshold", string.Create(ENGLISH_CULTURE, $"{chain.RifeSceneDetectThreshold ?? 0.015M}"));
                parser.SetValue(section, $"chain_{chain.ChainNumber}_rife_before_upscale", chain.RifeBeforeUpscale ? "yes" : "no");
            }

            return parser;
        }

        public void CheckAndDoBackup()
        {
            Task.Run(() =>
            {
                if (!Path.Exists(BackupPath))
                {
                    Directory.CreateDirectory(BackupPath);
                }

                var files = Directory.EnumerateFiles(BackupPath)
                            .Where(f => Path.GetFileName(f).StartsWith("autobackup_"))
                            .OrderByDescending(f => f)
                            .ToList();

                var currentConfStr = ParsedAnimeJaNaiConf(AnimeJaNaiConf)?.ToString();

                if (files.Count >= 10)
                {
                    // Delete oldest backup
                    File.Delete(files.Last());
                }

                if (files.Count > 0)
                {
                    var backupConf = ReadAnimeJaNaiConf(files.First());
                    if (currentConfStr == ParsedAnimeJaNaiConf(backupConf)?.ToString())
                    {
                        // Backup already exists - no need to create another backup
                        return;
                    }
                }

                WriteAnimeJaNaiConf(Path.Join(BackupPath, $"autobackup_{DateTime.Now:yyyyMMdd-HHmmss}.conf"));
            });
        }

#pragma warning disable CA1822 // Mark members as static
        public async void LaunchBenchmark()
#pragma warning restore CA1822 // Mark members as static
        {
            await Task.Run(async () =>
            {
                using var process = new Process();

                if (OperatingSystem.IsWindows())
                {
                    process.StartInfo.FileName = "cmd.exe";
                    process.StartInfo.Arguments = @"/C .\benchmarks\animejanai_benchmark_all.bat";
                }
                else
                {
                    // Linux: the same benchmark run via the cross-platform .NET tool.
                    process.StartInfo.FileName = "/bin/sh";
                    process.StartInfo.Arguments = "./benchmarks/animejanai_benchmark_all.sh";
                }

                process.StartInfo.RedirectStandardOutput = false;
                process.StartInfo.RedirectStandardError = false;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = false;
                process.StartInfo.WorkingDirectory = DataDir;

                process.Start();
                await process.WaitForExitAsync();
            });
        }

#pragma warning disable CA1822 // Mark members as static
        public async void OpenModelsDirectory()
#pragma warning restore CA1822 // Mark members as static
        {
            await Task.Run(() =>
            {
                // The shell opens folders with the platform's file manager (xdg-open on Linux).
                Process.Start(new ProcessStartInfo(OnnxPath) { UseShellExecute = true });
            });
        }

        public void SelectDefaultProfile()
        {
            if (SelectedMpvProfile == CurrentSlot.MpvProfileName)
            {
                // Toggling off the current default leaves the player upscaling-off by default.
                SelectedMpvProfile = null;
                WriteDefaultProfile(true);
            }
            else
            {
                SelectedMpvProfile = CurrentSlot.MpvProfileName;
                WriteDefaultProfile();
            }
        }

        // The default profile is stored as [global] default_slot in animejanai.conf and applied by
        // scripts/animejanai_backend.lua at startup. These map the Manager's mpv profile names to the
        // native filter slot numbers the player switches between: Quality/Balanced/Performance are the
        // built-in slots 1001/1002/1003, custom profiles use their own number 1-9, and Off is slot 0.
        private static int? MpvProfileToSlot(string? mpvProfile)
        {
            if (string.IsNullOrEmpty(mpvProfile))
            {
                return null;
            }
            switch (mpvProfile)
            {
                case "upscale-off": return 0;
                case "upscale-on-quality": return 1001;
                case "upscale-on-balanced": return 1002;
                case "upscale-on-performance": return 1003;
            }
            var m = Regex.Match(mpvProfile, @"^upscale-on-(\d+)$");
            return m.Success ? int.Parse(m.Groups[1].Value, ENGLISH_CULTURE) : (int?)null;
        }

        private static string? SlotToMpvProfile(int? slot)
        {
            switch (slot)
            {
                case null: return null;
                case 0: return "upscale-off";
                case 1001: return "upscale-on-quality";
                case 1002: return "upscale-on-balanced";
                case 1003: return "upscale-on-performance";
            }
            return slot.Value is >= 1 and <= 9 ? $"upscale-on-{slot.Value}" : null;
        }

        private string? ReadCurrentProfileFromMpvConf() => SlotToMpvProfile(AnimeJaNaiConf?.DefaultSlot);

        private void WriteDefaultProfile(bool upscaleOff = false)
        {
            var profile = upscaleOff ? "upscale-off" : CurrentSlot.MpvProfileName;
            AnimeJaNaiConf.DefaultSlot = MpvProfileToSlot(profile);
            WriteAnimeJaNaiConf();
        }

        private void InitializeSelectedSlot()
        {
            if (SelectedMpvProfile == null || SelectedMpvProfile == "upscale-off")
            {
                return;
            }

            var defaultSlot = DefaultUpscaleSlots.Where(x => x.MpvProfileName == SelectedMpvProfile).FirstOrDefault();

            if (defaultSlot != null)
            {
                HandleShowDefaultProfile(defaultSlot.SlotNumber);
                return;
            }

            var customSlot = AnimeJaNaiConf.UpscaleSlots.Where(x => x.MpvProfileName == SelectedMpvProfile).FirstOrDefault();

            if (customSlot != null)
            {
                HandleShowCustomProfile(customSlot.SlotNumber);
            }
        }
    }

    [DataContract]
    public class AnimeJaNaiConf : ReactiveObject
    {
        public AnimeJaNaiConf(bool autoSave = false)
        {
            Debug.WriteLine($"autoSave? {autoSave}");
            if (autoSave)
            {
                this.WhenAnyValue(
                    x => x.EnableLogging,
                    x => x.TensorRtSelected,
                    x => x.DirectMlSelected,
                    x => x.BackendAutoFallback,
                    x => x.TrtEngineSettings).Subscribe(x =>
                    {
                        Vm?.WriteAnimeJaNaiConf();
                    });

                // Separate subscription: WhenAnyValue's tuple overload doesn't extend to this many
                // properties in one call.
                this.WhenAnyValue(
                    x => x.QualitySharp,
                    x => x.BalancedSharp,
                    x => x.PerformanceSharp,
                    x => x.GpuSubtitles,
                    x => x.AutoDisplayRefresh).Subscribe(x =>
                    {
                        Vm?.WriteAnimeJaNaiConf();
                    });

                this.WhenAnyValue(
                    x => x.GpuTensorRt,
                    x => x.GpuDirectMl).Subscribe(x =>
                    {
                        Vm?.WriteAnimeJaNaiConf();
                    });

            }
        }

        private MainWindowViewModel? _vm;
        public MainWindowViewModel? Vm
        {
            get => _vm;
            set => this.RaiseAndSetIfChanged(ref _vm, value);
        }

        private bool _enableLogging = false;
        [DataMember]
        public bool EnableLogging
        {
            get => _enableLogging;
            set => this.RaiseAndSetIfChanged(ref _enableLogging, value);
        }

        // The slot the player loads by default at startup (Quality/Balanced/Performance = 1001/1002/
        // 1003, custom profiles = 1-9, Off = 0). null means no default is stored, so the slot baked
        // into the vf line (Balanced) applies. Persisted as [global] default_slot in animejanai.conf
        // and applied by scripts/animejanai_backend.lua. Not auto-saved here; the Profiles tab writes
        // it explicitly via SelectDefaultProfile.
        private int? _defaultSlot;
        public int? DefaultSlot
        {
            get => _defaultSlot;
            set => this.RaiseAndSetIfChanged(ref _defaultSlot, value);
        }

        private bool _tensorRtSelected = true;
        [DataMember]
        public bool TensorRtSelected
        {
            get => _tensorRtSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _tensorRtSelected, value);
            }
        }

        private bool _directMlSelected = false;
        [DataMember]
        public bool DirectMlSelected
        {
            get => _directMlSelected;
            set
            {
                this.RaiseAndSetIfChanged(ref _directMlSelected, value);
            }
        }

        // Per-profile Standard vs Sharp model selection for the three built-in default profiles.
        // Persisted as [global] quality_preset / balanced_preset / performance_preset and honored at
        // playback by animejanai_config.py (slots 1001/1002/1003). false => standard, true => sharp.
        private bool _qualitySharp = false;
        [DataMember]
        public bool QualitySharp
        {
            get => _qualitySharp;
            set => this.RaiseAndSetIfChanged(ref _qualitySharp, value);
        }

        private bool _balancedSharp = false;
        [DataMember]
        public bool BalancedSharp
        {
            get => _balancedSharp;
            set => this.RaiseAndSetIfChanged(ref _balancedSharp, value);
        }

        private bool _performanceSharp = false;
        [DataMember]
        public bool PerformanceSharp
        {
            get => _performanceSharp;
            set => this.RaiseAndSetIfChanged(ref _performanceSharp, value);
        }

        private string _trtEngineSettings = "--builderOptimizationLevel=5 --optShapes=input:%video_resolution% --skipInference";
        [DataMember]
        public string TrtEngineSettings
        {
            get => _trtEngineSettings;
            set
            {
                this.RaiseAndSetIfChanged(ref _trtEngineSettings, value);
                this.RaisePropertyChanged(nameof(TrtStaticOnnxSelected));
                this.RaisePropertyChanged(nameof(TrtStaticSelected));
                this.RaisePropertyChanged(nameof(TrtDynamicSelected));
                this.RaisePropertyChanged(nameof(TrtDynamicMinResolution));
                this.RaisePropertyChanged(nameof(TrtDynamicOptResolution));
                this.RaisePropertyChanged(nameof(TrtDynamicMaxResolution));
                this.RaisePropertyChanged(nameof(TrtBuilderOptimizationLevel));
                this.RaisePropertyChanged(nameof(TrtSummary));
            }
        }

        public string TrtSummary =>
            (TrtDynamicSelected ? $"Dynamic {TrtDynamicMinResolution} - {TrtDynamicMaxResolution}"
             : TrtStaticSelected ? "Static" : "Static ONNX") +
            $" · optimization level {TrtBuilderOptimizationLevel}";

        public bool TrtDynamicSelected => TrtEngineSettings.Contains("--minShapes=");
        public bool TrtStaticSelected => TrtEngineSettings.Contains("--optShapes=") && !TrtDynamicSelected;
        public bool TrtStaticOnnxSelected => !TrtEngineSettings.Contains("--optShapes=") && !TrtDynamicSelected;

        private static string ShapeToResolution(string shapeArg)
        {
            var match = Regex.Match(shapeArg, @"input:1x3x(\d+)x(\d+)");
            if (match.Success)
                return $"{match.Groups[2].Value}x{match.Groups[1].Value}";
            return "0x0";
        }

        private static string ResolutionToShape(string resolution)
        {
            var match = Regex.Match(resolution, @"(\d+)x(\d+)");
            if (match.Success)
                return $"1x3x{match.Groups[2].Value}x{match.Groups[1].Value}";
            return "1x3x0x0";
        }

        private static string? ExtractShapeValue(string settings, string prefix)
        {
            var match = Regex.Match(settings, prefix + @"=(\S+)");
            return match.Success ? match.Groups[1].Value : null;
        }

        public string TrtDynamicMinResolution
        {
            get
            {
                var val = ExtractShapeValue(TrtEngineSettings, "--minShapes");
                return val != null ? ShapeToResolution(val) : "8x8";
            }
            set
            {
                var shape = ResolutionToShape(value);
                TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--minShapes=\S+", $"--minShapes=input:{shape}");
            }
        }

        public string TrtDynamicOptResolution
        {
            get
            {
                if (!TrtDynamicSelected) return "1920x1080";
                var val = ExtractShapeValue(TrtEngineSettings, "--optShapes");
                return val != null ? ShapeToResolution(val) : "1920x1080";
            }
            set
            {
                var shape = ResolutionToShape(value);
                if (TrtDynamicSelected)
                    TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--optShapes=\S+", $"--optShapes=input:{shape}");
            }
        }

        public string TrtDynamicMaxResolution
        {
            get
            {
                var val = ExtractShapeValue(TrtEngineSettings, "--maxShapes");
                return val != null ? ShapeToResolution(val) : "1920x1080";
            }
            set
            {
                var shape = ResolutionToShape(value);
                TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--maxShapes=\S+", $"--maxShapes=input:{shape}");
            }
        }

        public string TrtBuilderOptimizationLevel
        {
            get
            {
                var match = Regex.Match(TrtEngineSettings, @"--builderOptimizationLevel=(\d+)");
                return match.Success ? match.Groups[1].Value : "5";
            }
            set
            {
                var match = Regex.Match(value ?? "", @"\d+");
                var level = match.Success ? match.Value : "5";
                if (Regex.IsMatch(TrtEngineSettings, @"--builderOptimizationLevel=\d+"))
                    TrtEngineSettings = Regex.Replace(TrtEngineSettings, @"--builderOptimizationLevel=\d+", $"--builderOptimizationLevel={level}");
                else
                    TrtEngineSettings = InsertBeforeTactics(TrtEngineSettings, $"--builderOptimizationLevel={level}");
            }
        }

        private static string RemoveShapeArgs(string settings)
        {
            var result = Regex.Replace(settings, @"\s*--(?:min|opt|max)Shapes=\S+", "");
            return Regex.Replace(result, @"\s+", " ").Trim();
        }

        private static string InsertBeforeTactics(string settings, string toInsert)
        {
            var idx = settings.IndexOf("--tacticSources");
            if (idx >= 0)
                return settings.Insert(idx, toInsert + " ");
            return settings + " " + toInsert;
        }

        public void SetTrtStaticOnnx()
        {
            TrtEngineSettings = RemoveShapeArgs(TrtEngineSettings);
        }

        public void SetTrtStatic()
        {
            var settings = RemoveShapeArgs(TrtEngineSettings);
            TrtEngineSettings = InsertBeforeTactics(settings, "--optShapes=input:%video_resolution%");
        }

        public void SetTrtDynamic()
        {
            var settings = RemoveShapeArgs(TrtEngineSettings);
            TrtEngineSettings = InsertBeforeTactics(settings, "--minShapes=input:1x3x8x8 --optShapes=input:1x3x1080x1920 --maxShapes=input:1x3x1080x1920");
        }

        private AvaloniaList<UpscaleSlot> _upscaleSlots = [];
        [DataMember]
        public AvaloniaList<UpscaleSlot> UpscaleSlots
        {
            get => _upscaleSlots;
            set
            {
                this.RaiseAndSetIfChanged(ref _upscaleSlots, value);
                Vm?.RaisePropertyChanged("AllSlots");
            }
        }

        public void SetTensorRtSelected()
        {
            TensorRtSelected = true;
            DirectMlSelected = false;
        }

        public void SetDirectMlSelected()
        {
            DirectMlSelected = true;
            TensorRtSelected = false;
        }

        public void UserSelectTensorRt()
        {
            BackendAutoFallback = false;
            SetTensorRtSelected();
        }

        public void UserSelectDirectMl()
        {
            BackendAutoFallback = false;
            SetDirectMlSelected();
        }

        private bool _backendAutoFallback;
        [DataMember]
        public bool BackendAutoFallback
        {
            get => _backendAutoFallback;
            set => this.RaiseAndSetIfChanged(ref _backendAutoFallback, value);
        }

        // Experimental GPU subtitle rendering ([global] sub_render_mode=gpu). Off => the player
        // uses the stable subtitle defaults from the managed [animejanai] profile.
        private bool _gpuSubtitles;
        [DataMember]
        public bool GpuSubtitles
        {
            get => _gpuSubtitles;
            set => this.RaiseAndSetIfChanged(ref _gpuSubtitles, value);
        }

        public Backend SelectedBackend => DirectMlSelected ? Backend.DirectML : Backend.TensorRT;

        // The GPU choice per backend, "" for Auto. [global] gpu_tensorrt is a Vulkan/CUDA device
        // UUID and gpu_directml a D3D11 adapter name; scripts/animejanai_backend.lua applies the
        // active backend's one as --vulkan-device / --d3d11-adapter. One per backend because
        // they name GPUs differently, and so switching backends keeps each choice.
        private string _gpuTensorRt = "";
        [DataMember]
        public string GpuTensorRt
        {
            get => _gpuTensorRt;
            set => this.RaiseAndSetIfChanged(ref _gpuTensorRt, value);
        }

        private string _gpuDirectMl = "";
        [DataMember]
        public string GpuDirectMl
        {
            get => _gpuDirectMl;
            set => this.RaiseAndSetIfChanged(ref _gpuDirectMl, value);
        }

        private bool _autoDisplayRefresh;
        [DataMember]
        public bool AutoDisplayRefresh
        {
            get => _autoDisplayRefresh;
            set => this.RaiseAndSetIfChanged(ref _autoDisplayRefresh, value);
        }
    }

    [DataContract]
    public class UpscaleSlot : ReactiveObject
    {
        public UpscaleSlot(bool autoSave = false)
        {
            if (autoSave)
            {
                this.WhenAnyValue(
                    x => x.SlotNumber,
                    x => x.ProfileName,
                    x => x.Chains.Count
                ).Subscribe(x =>
                {
                    Vm?.WriteAnimeJaNaiConf();
                });
            }

            // ReactiveUI 26's WhenAnyValue throws on a null source, and Vm starts out null
            this.WhenAnyValue(x => x.Vm).Subscribe(vm =>
            {
                sub?.Dispose();
                sub2?.Dispose();
                sub = sub2 = null;
                if (vm is null)
                    return;

                sub = vm.WhenAnyValue(
                    x => x.SelectedSlotNumber,
                    x => x.ShowCustomProfiles,
                    x => x.ShowDefaultProfiles
                    ).Subscribe(x =>
                {
                    this.RaisePropertyChanged(nameof(ActiveSlot));
                    Vm?.RaisePropertyChanged("AllSlots");
                });

                sub2 = vm.WhenAnyValue(x => x.SelectedMpvProfile).Subscribe(x =>
                {
                    this.RaisePropertyChanged(nameof(IsSelectedMpvProfile));
                    this.RaisePropertyChanged(nameof(DefaultMark));
                });
            });
        }

        private IDisposable? sub;
        private IDisposable? sub2;

        private MainWindowViewModel? _vm;
        public MainWindowViewModel? Vm
        {
            get => _vm;
            set => this.RaiseAndSetIfChanged(ref _vm, value);
        }

        private string _slotNumber = string.Empty;
        [DataMember]
        public string SlotNumber
        {
            get => _slotNumber;
            set => this.RaiseAndSetIfChanged(ref _slotNumber, value);
        }

        public bool ActiveSlot => ((Vm?.ShowCustomProfiles ?? false) && IsCustomSlot || (Vm?.ShowDefaultProfiles ?? false) && !IsCustomSlot) && SlotNumber == Vm?.SelectedSlotNumber;

        public bool IsCustomSlot => MpvProfileName?.Any(char.IsDigit) ?? false;

        public string SlotIcon => $"Number{SlotNumber}Circle";

        public bool ShowSlot => int.Parse(_slotNumber) < 10;

        private string _profileName = string.Empty;
        [DataMember]
        public string ProfileName
        {
            get => _profileName;
            set => this.RaiseAndSetIfChanged(ref _profileName, value);
        }

        private string _descriptionText = string.Empty;
        [DataMember]
        public string DescriptionText
        {
            get => _descriptionText;
            set => this.RaiseAndSetIfChanged(ref _descriptionText, value);
        }

        private string _mpvProfileName;
        [DataMember]
        public string MpvProfileName
        {
            get => _mpvProfileName;
            set => this.RaiseAndSetIfChanged(ref _mpvProfileName, value);
        }

        public bool IsSelectedMpvProfile => MpvProfileName == Vm?.SelectedMpvProfile;

        // the sidebar marks the profile mpv starts with
        public string DefaultMark => IsSelectedMpvProfile ? " (Default)" : "";

        private AvaloniaList<UpscaleChain> _chains = [];
        [DataMember]
        public AvaloniaList<UpscaleChain> Chains
        {
            get => _chains;
            set => this.RaiseAndSetIfChanged(ref _chains, value);
        }
    }

    [DataContract]
    public class UpscaleChain : ReactiveObject
    {
        public UpscaleChain(bool autoSave = false)
        {
            AutoSave = autoSave;

            if (autoSave)
            {
                var g1 = this.WhenAnyValue(
                    x => x.ChainNumber,
                    x => x.MinResolution,
                    x => x.MaxResolution,
                    x => x.MinFps,
                    x => x.MaxFps,
                    x => x.Models.Count,
                    x => x.EnableRife
                );

                var g2 = this.WhenAnyValue(
                    x => x.RifeFactorNumerator,
                    x => x.RifeFactorDenominator,
                    x => x.RifeModel,
                    x => x.RifeSceneDetectThreshold,
                    x => x.RifeEnsemble,
                    x => x.RifeBeforeUpscale
                );

                var g3 = this.WhenAnyValue(
                    x => x.EnableUpscale,
                    x => x.EnableTemporalFix,
                    x => x.TemporalFixStrength,
                    x => x.TemporalFixSceneThreshold,
                    x => x.TemporalFixBeforeUpscale
                );

                g1.CombineLatest(g2, g3).Subscribe(x =>
                {
                    Vm?.WriteAnimeJaNaiConf();
                });
            }

            // the one-line summaries in the section headers follow every setting they describe
            PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Models))
                    HookModels();
                if (e.PropertyName == nameof(ChainNumber))
                {
                    // renumbered after a removal: the remembered states are keyed by number
                    this.RaisePropertyChanged(nameof(ChainSectionExpanded));
                    this.RaisePropertyChanged(nameof(ActivationSectionExpanded));
                    this.RaisePropertyChanged(nameof(UpscaleSectionExpanded));
                    this.RaisePropertyChanged(nameof(TemporalFixSectionExpanded));
                    this.RaisePropertyChanged(nameof(RifeSectionExpanded));
                }
                if (e.PropertyName is { } name && !name.EndsWith("Summary") && !name.EndsWith("Expanded"))
                    RaiseSummaries();
            };
            HookModels();
        }

        public bool AutoSave { get; set; }

        private AvaloniaList<UpscaleModel>? _hookedModels;

        private void HookModels()
        {
            if (_hookedModels != null)
            {
                _hookedModels.CollectionChanged -= ModelsCollectionChanged;
                foreach (var model in _hookedModels)
                    model.PropertyChanged -= ModelPropertyChanged;
            }
            _hookedModels = Models;
            _hookedModels.CollectionChanged += ModelsCollectionChanged;
            foreach (var model in _hookedModels)
                model.PropertyChanged += ModelPropertyChanged;
        }

        private void ModelsCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            foreach (UpscaleModel model in e.OldItems ?? Array.Empty<UpscaleModel>())
                model.PropertyChanged -= ModelPropertyChanged;
            foreach (UpscaleModel model in e.NewItems ?? Array.Empty<UpscaleModel>())
                model.PropertyChanged += ModelPropertyChanged;
            RaiseSummaries();
        }

        private void ModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(UpscaleModel.Name))
                RaiseSummaries();
        }

        private void RaiseSummaries()
        {
            this.RaisePropertyChanged(nameof(ChainSummary));
            this.RaisePropertyChanged(nameof(ActivationSummary));
            this.RaisePropertyChanged(nameof(UpscaleSummary));
            this.RaisePropertyChanged(nameof(TemporalFixSummary));
            this.RaisePropertyChanged(nameof(RifeSummary));
        }

        // ---- collapsible sections ----------------------------------------------------------
        // Expanded state lives in manager-ui.json, keyed by profile and chain number. A chain
        // that isn't in a profile yet (still being read from the conf) has no key, so loading
        // never touches it.

        private string? SectionKey(string section)
        {
            if (Vm == null)
                return null;
            if (Vm.AnimeJaNaiConf?.UpscaleSlots.FirstOrDefault(s => s.Chains.Contains(this)) is { } custom)
                return $"profile{custom.SlotNumber}:chain{ChainNumber}:{section}";
            if (Vm.DefaultUpscaleSlots?.FirstOrDefault(s => s.Chains.Contains(this)) is { } builtin)
                return $"default{builtin.SlotNumber}:chain{ChainNumber}:{section}";
            return null;
        }

        private bool GetExpanded(string section, bool fallback) =>
            SectionKey(section) is { } key ? UiState.Current.GetExpanded(key) ?? fallback : fallback;

        private void SetExpanded(string section, bool value, string property)
        {
            if (SectionKey(section) is { } key)
                UiState.Current.SetExpanded(key, value);
            this.RaisePropertyChanged(property);
        }

        public bool ChainSectionExpanded
        {
            get => GetExpanded("chain", true);
            set => SetExpanded("chain", value, nameof(ChainSectionExpanded));
        }

        public bool ActivationSectionExpanded
        {
            get => GetExpanded("activation", true);
            set => SetExpanded("activation", value, nameof(ActivationSectionExpanded));
        }

        public bool UpscaleSectionExpanded
        {
            get => GetExpanded("upscale", EnableUpscale);
            set => SetExpanded("upscale", value, nameof(UpscaleSectionExpanded));
        }

        public bool TemporalFixSectionExpanded
        {
            get => GetExpanded("tfix", EnableTemporalFix);
            set => SetExpanded("tfix", value, nameof(TemporalFixSectionExpanded));
        }

        public bool RifeSectionExpanded
        {
            get => GetExpanded("rife", EnableRife);
            set => SetExpanded("rife", value, nameof(RifeSectionExpanded));
        }

        // ---- header summaries ------------------------------------------------------------------

        private static string RangeText(string min, string max, string any, string unit = "")
        {
            bool noMin = string.IsNullOrEmpty(min) || min == "0" || min == "0x0";
            bool noMax = string.IsNullOrEmpty(max) || max == "0" || max == "0x0";
            return (noMin, noMax) switch
            {
                (true, true) => any,
                (false, true) => $"{min}+{unit}",
                (true, false) => $"up to {max}{unit}",
                _ => $"{min} - {max}{unit}",
            };
        }

        // "2x_AnimeJaNai_HD_V3.1_Balanced_SPANF3_b8f64_unshuffle_fp16" -> "HD_V3.1_Balanced"
        public static string ShortModelName(string? name)
        {
            if (string.IsNullOrEmpty(name))
                return "(no model)";
            var shortName = Regex.Replace(name, @"^\d+x_", "");
            shortName = Regex.Replace(shortName, @"^AnimeJaNai_", "", RegexOptions.IgnoreCase);
            var cut = Regex.Match(shortName, @"_(SPANF\d|unshuffle|fp16|fp32|op\d+|\d+x\d+xHxW|dyn-HW)", RegexOptions.IgnoreCase);
            return cut.Success && cut.Index > 0 ? shortName[..cut.Index] : shortName;
        }

        public string ChainSummary
        {
            get
            {
                var steps = EnableUpscale ? Models.Select(m => ShortModelName(m.Name)).ToList() : [];
                if (EnableTemporalFix)
                    steps.Add($"Temporal Fix {TemporalFixStrength}");
                if (EnableRife)
                    steps.Add($"RIFE {RifeFactorNumerator}/{RifeFactorDenominator}");
                var fps = RangeText(MinFps, MaxFps, "any fps", " fps");
                return $"{RangeText(MinResolution, MaxResolution, "any resolution")} · {fps} · " +
                       (steps.Count > 0 ? string.Join(" → ", steps) : "passthrough");
            }
        }

        public string ActivationSummary =>
            $"{RangeText(MinResolution, MaxResolution, "any resolution")} · {RangeText(MinFps, MaxFps, "any fps", " fps")}";

        public string UpscaleSummary => !EnableUpscale ? "Off"
            : Models.Count > 0 ? string.Join(" → ", Models.Select(m => ShortModelName(m.Name)))
            : "no models";

        public string TemporalFixSummary => !EnableTemporalFix ? "Off"
            : $"Strength {TemporalFixStrength}" + (EnableUpscale && TemporalFixBeforeUpscale ? " · before upscaling" : "");

        public string RifeSummary => EnableRife
            ? $"{RifeModel} · {RifeFactorNumerator}/{RifeFactorDenominator}" + (RifeEnsemble ? " · ensemble" : "")
            : "Off";

        public MainWindowViewModel? Vm { get; set; }

        private string _chainNumber = string.Empty;
        [DataMember]
        public string ChainNumber
        {
            get => _chainNumber;
            set => this.RaiseAndSetIfChanged(ref _chainNumber, value);
        }

        private string _minResolution = "0x0";
        [DataMember]
        public string MinResolution
        {
            get => _minResolution;
            set => this.RaiseAndSetIfChanged(ref _minResolution, value);
        }

        private string _maxResolution = "0x0";
        [DataMember]
        public string MaxResolution
        {
            get => _maxResolution;
            set => this.RaiseAndSetIfChanged(ref _maxResolution, value);
        }

        private string _minFps = 0.ToString();
        [DataMember]
        public string MinFps
        {
            get => _minFps;
            set => this.RaiseAndSetIfChanged(ref _minFps, value);
        }

        private string _maxFps = 0.ToString();
        [DataMember]
        public string MaxFps
        {
            get => _maxFps;
            set => this.RaiseAndSetIfChanged(ref _maxFps, value);
        }

        private AvaloniaList<UpscaleModel> _models = [];
        [DataMember]
        public AvaloniaList<UpscaleModel> Models
        {
            get => _models;
            set => this.RaiseAndSetIfChanged(ref _models, value);
        }

        private bool _enableRife = false;
        [DataMember]
        public bool EnableRife
        {
            get => _enableRife;
            set
            {
                bool changed = _enableRife != value;
                this.RaiseAndSetIfChanged(ref _enableRife, value);
                this.RaisePropertyChanged(nameof(RifeToggleEnabled));
                // switching the feature on opens its section, off closes it
                if (changed && SectionKey("rife") != null)
                    RifeSectionExpanded = value;
            }
        }

        // Enabling RIFE needs the models; disabling it must always be possible.
        // So the toggle locks only in the unchecked-and-not-installed state.
        public bool RifeToggleEnabled =>
            EnableRife || !(MainWindowViewModel.Instance?.RifeMissing ?? false);

        private List<string> _rifeModelList = new(MainWindowViewModel.RifeModels);

        public List<string> RifeModelList
        {
            get => _rifeModelList;
            set => this.RaiseAndSetIfChanged(ref _rifeModelList, value);
        }

        private string _rifeModel = MainWindowViewModel.RifeModels.FirstOrDefault("");
        [DataMember]
        public string RifeModel
        {
            get => _rifeModel;
            set => this.RaiseAndSetIfChanged(ref _rifeModel, value);
        }

        private bool _rifeEnsemble = false;
        [DataMember]
        public bool RifeEnsemble
        {
            get => _rifeEnsemble;
            set => this.RaiseAndSetIfChanged(ref _rifeEnsemble, value);
        }

        private int? _rifeFactorNumerator = 2;
        [DataMember]
        public int? RifeFactorNumerator
        {
            get => _rifeFactorNumerator ?? 2;
            set => this.RaiseAndSetIfChanged(ref _rifeFactorNumerator, value ?? 2);
        }

        private int? _rifeFactorDenominator = 1;
        [DataMember]
        public int? RifeFactorDenominator
        {
            get => _rifeFactorDenominator ?? 1;
            set => this.RaiseAndSetIfChanged(ref _rifeFactorDenominator, value ?? 1);
        }

        private decimal? _rifeSceneDetectThreshold = 0.150M;
        [DataMember]
        public decimal? RifeSceneDetectThreshold
        {
            get => _rifeSceneDetectThreshold;
            set => this.RaiseAndSetIfChanged(ref _rifeSceneDetectThreshold, value ?? 0.150M);
        }

        // Interpolate at the source resolution, then upscale (faster); default
        // on. False = interpolate the upscaled frames (the old order).
        private bool _rifeBeforeUpscale = true;
        [DataMember]
        public bool RifeBeforeUpscale
        {
            get => _rifeBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _rifeBeforeUpscale, value);
        }

        // Upscaling off (chain_N_upscale=no) keeps the models in the conf; the engine skips them
        // and runs only temporal fix / RIFE at the source resolution.
        private bool _enableUpscale = true;
        [DataMember]
        public bool EnableUpscale
        {
            get => _enableUpscale;
            set
            {
                bool changed = _enableUpscale != value;
                this.RaiseAndSetIfChanged(ref _enableUpscale, value);
                if (changed && SectionKey("upscale") != null)
                    UpscaleSectionExpanded = value;
            }
        }

        // Temporal fix: a vs_temporalfix model the engine runs after the chain's models, or
        // before them (chain_N_temporal_fix_model; see MainWindowViewModel.ReadChainFeatures).
        private bool _enableTemporalFix = false;
        [DataMember]
        public bool EnableTemporalFix
        {
            get => _enableTemporalFix;
            set
            {
                bool changed = _enableTemporalFix != value;
                this.RaiseAndSetIfChanged(ref _enableTemporalFix, value);
                this.RaisePropertyChanged(nameof(TemporalFixToggleEnabled));
                if (changed && SectionKey("tfix") != null)
                    TemporalFixSectionExpanded = value;
            }
        }

        // Same rule as RifeToggleEnabled: locked only while unchecked and not installed.
        public bool TemporalFixToggleEnabled =>
            EnableTemporalFix || !(MainWindowViewModel.Instance?.TemporalFixMissing ?? false);

        public static List<int> TemporalFixStrengths => MainWindowViewModel.TemporalFixStrengths;

        private int _temporalFixStrength = 2;
        [DataMember]
        public int TemporalFixStrength
        {
            get => _temporalFixStrength;
            set => this.RaiseAndSetIfChanged(ref _temporalFixStrength, value);
        }

        // Run temporal fix at the source resolution before the upscale models (much cheaper)
        // instead of on the upscaled frames (also steadies shimmer the upscaler adds).
        public const bool DefaultTemporalFixBeforeUpscale = true;

        private bool _temporalFixBeforeUpscale = DefaultTemporalFixBeforeUpscale;
        [DataMember]
        public bool TemporalFixBeforeUpscale
        {
            get => _temporalFixBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _temporalFixBeforeUpscale, value);
        }

        private decimal? _temporalFixSceneThreshold = decimal.Parse(UpscaleModel.DefaultTemporalSceneThreshold, CultureInfo.InvariantCulture);
        [DataMember]
        public decimal? TemporalFixSceneThreshold
        {
            get => _temporalFixSceneThreshold;
            set => this.RaiseAndSetIfChanged(ref _temporalFixSceneThreshold, value ?? decimal.Parse(UpscaleModel.DefaultTemporalSceneThreshold, CultureInfo.InvariantCulture));
        }

        public void AddModel()
        {
            Models.Add(new UpscaleModel(AutoSave)
            {
                Vm = Vm,
                AllModels = Vm?.GetAllModels(),
            });

            UpdateModelHeaders();
        }

        public void DeleteModel(UpscaleModel model)
        {
            try
            {
                Models.Remove(model);
            }
            catch (ArgumentOutOfRangeException)
            {

            }

            UpdateModelHeaders();
        }

        public void UpdateModelHeaders()
        {
            for (var i = 0; i < Models.Count; i++)
            {
                Models[i].ModelNumber = (i + 1).ToString();
            }
        }

    }

    [DataContract]
    public class UpscaleModel : ReactiveObject
    {
        public UpscaleModel(bool autoSave = false)
        {
            if (autoSave)
            {
                this.WhenAnyValue(
                    x => x.ModelNumber,
                    x => x.ResizeHeightBeforeUpscale,
                    x => x.ResizeFactorBeforeUpscale,
                    x => x.Name,
                    x => x.Frames,
                    x => x.TemporalSceneThreshold
                ).Subscribe(x =>
                {
                    Vm?.WriteAnimeJaNaiConf();
                });
            }
        }

        public MainWindowViewModel? Vm { get; set; }

        private AvaloniaList<string> _allModels = [];
        [DataMember]
        public AvaloniaList<string> AllModels
        {
            get => _allModels;
            set => this.RaiseAndSetIfChanged(ref _allModels, value);
        }

        private string _modelNumber = string.Empty;
        [DataMember]
        public string ModelNumber
        {
            get => _modelNumber;
            set => this.RaiseAndSetIfChanged(ref _modelNumber, value);
        }

        private string _resizeHeightBeforeUpscale = 0.ToString();
        [DataMember]
        public string ResizeHeightBeforeUpscale
        {
            get => _resizeHeightBeforeUpscale;
            set
            {
                this.RaiseAndSetIfChanged(ref _resizeHeightBeforeUpscale, value);
                this.RaisePropertyChanged(nameof(ResizeFactorBeforeUpscaleIsEnabled));
            }
        }

        private string _resizeFactorBeforeUpscale = 100.ToString();
        [DataMember]
        public string ResizeFactorBeforeUpscale
        {
            get => _resizeFactorBeforeUpscale;
            set => this.RaiseAndSetIfChanged(ref _resizeFactorBeforeUpscale, value);
        }

        public bool ResizeFactorBeforeUpscaleIsEnabled => ResizeHeightBeforeUpscale == 0.ToString();

        private string _name = string.Empty;
        [DataMember]
        public string Name
        {
            get => _name;
            set
            {
                this.RaiseAndSetIfChanged(ref _name, value);
                RaiseTemporalChanged();
            }
        }

        // Temporal (multi-frame) models: the engine detects the frame count
        // from the onnx input; Frames overrides it when nonzero.
        public const string DefaultTemporalSceneThreshold = "0.15";

        private string _frames = 0.ToString();
        [DataMember]
        public string Frames
        {
            get => _frames;
            set
            {
                this.RaiseAndSetIfChanged(ref _frames, value);
                RaiseTemporalChanged();
            }
        }

        private string _temporalSceneThreshold = DefaultTemporalSceneThreshold;
        [DataMember]
        public string TemporalSceneThreshold
        {
            get => _temporalSceneThreshold;
            set => this.RaiseAndSetIfChanged(ref _temporalSceneThreshold, value);
        }

        public int TemporalFrames
        {
            get
            {
                if (int.TryParse(Frames, NumberStyles.Integer, CultureInfo.InvariantCulture, out var frames) && frames > 0)
                    return frames;
                if (Vm == null || string.IsNullOrEmpty(Name))
                    return 1;
                return OnnxInputShape.TemporalFrames(Path.Combine(Vm.OnnxPath, Name + ".onnx"));
            }
        }

        public bool IsTemporal => TemporalFrames > 1;

        private void RaiseTemporalChanged()
        {
            this.RaisePropertyChanged(nameof(TemporalFrames));
            this.RaisePropertyChanged(nameof(IsTemporal));
        }
    }

    public enum Backend
    {
        TensorRT,
        DirectML,
        NCNN
    }
}
