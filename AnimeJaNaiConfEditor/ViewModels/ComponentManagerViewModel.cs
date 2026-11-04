using Avalonia.Collections;
using Avalonia.Threading;
using ReactiveUI;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.ViewModels
{
    // One installable component pack (TensorRT runtime, per-GPU-generation kernels, RIFE
    // models), as reported by `AnimeJaNaiUpdater.exe --components --json`.
    public class ComponentItem : ViewModelBase
    {
        public string Name { get; init; } = "";
        public long Bytes { get; init; }
        public bool Installed { get; init; }
        public bool Recommended { get; init; }
        public bool Preselect { get; init; }

        private bool _selected;
        public bool Selected
        {
            get => _selected;
            set => this.RaiseAndSetIfChanged(ref _selected, value);
        }

        public string SizeText => $"{Bytes / 1048576:N0} MB";

        public string StateText => Installed ? "installed" : "optional";

        // accent-tagged in the UI instead of pre-checked: recommendations should
        // be visible, not pre-decided
        public bool HighlightRecommended => Recommended && !Installed;

        public string Title => Name switch
        {
            "trt-runtime" => "TensorRT runtime",
            "rife" => "RIFE interpolation models",
            "trt-ptx" => "TensorRT kernels: other NVIDIA GPUs",
            _ when Name.StartsWith("trt-sm") => $"TensorRT kernels: {SmFamily(Name[6..])}",
            _ => Name,
        };

        public string Description => Name switch
        {
            "trt-runtime" => "The fastest upscaling engine, for NVIDIA GPUs. Without it, NVIDIA users fall back to the slower DirectML engine.",
            "rife" => "Frame interpolation (e.g. 24 → 48 fps). Not needed if you only upscale.",
            "trt-ptx" => "Fallback kernels for NVIDIA GPUs without a dedicated kernel pack. First engine build is slower.",
            _ when Name.StartsWith("trt-sm") => "Engine-builder kernels matched to this GPU generation. Only needed on these GPUs.",
            _ => "",
        };

        private static string SmFamily(string sm) => sm switch
        {
            "75" => "GeForce RTX 20 series (Turing)",
            "80" or "86" => "GeForce RTX 30 series (Ampere)",
            "89" => "GeForce RTX 40 series (Ada)",
            "90" => "Hopper",
            "100" or "120" => "GeForce RTX 50 series (Blackwell)",
            _ => $"sm{sm}",
        };
    }

    // Detects, installs, and removes component packs by shelling out to the updater, which
    // owns all pack logic (release lookup, NVML GPU detection, components.json bookkeeping).
    public class ComponentManagerViewModel : ViewModelBase
    {
        // The Linux package ships the updater without an extension.
        public static string UpdaterPath { get; } = Path.Combine(MainWindowViewModel.RootDir,
            OperatingSystem.IsWindows() ? "AnimeJaNaiUpdater.exe" : "AnimeJaNaiUpdater");

        public bool UpdaterFound => File.Exists(UpdaterPath);

        public AvaloniaList<ComponentItem> Packs { get; } = [];

        // GPU identity from the engine's NVML detection; null until a refresh
        // has completed (callers fall back to disk-state-only behavior).
        public bool? GpuNvidia { get; private set; }

        public bool TrtPackAvailable { get; private set; }

        // Each NVIDIA GPU (name, Vulkan/CUDA device UUID), in NVML's order, for the TensorRT GPU
        // choice. Empty until a refresh completes, without an NVIDIA GPU, and with an updater from
        // before the list.
        public IReadOnlyList<(string Name, string Uuid)> NvidiaGpus { get; private set; } = [];

        // Raised on the UI thread after every refresh (including after Apply),
        // so the Profiles tab can re-derive its component awareness.
        public event Action? Refreshed;

        private string _gpuText = "Detecting hardware...";
        public string GpuText
        {
            get => _gpuText;
            set => this.RaiseAndSetIfChanged(ref _gpuText, value);
        }

        private string _statusLine = "";
        public string StatusLine
        {
            get => _statusLine;
            set => this.RaiseAndSetIfChanged(ref _statusLine, value);
        }

        private bool _setupNeeded;
        public bool SetupNeeded
        {
            get => _setupNeeded;
            set => this.RaiseAndSetIfChanged(ref _setupNeeded, value);
        }

        private bool _isBusy;
        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                this.RaiseAndSetIfChanged(ref _isBusy, value);
                this.RaisePropertyChanged(nameof(NotBusy));
            }
        }

        public bool NotBusy => !IsBusy;

        private bool _loadFailed;
        public bool LoadFailed
        {
            get => _loadFailed;
            set => this.RaiseAndSetIfChanged(ref _loadFailed, value);
        }

        // Populates the pack list. Recommended-but-missing packs come back pre-checked, so
        // first-time setup is "uncheck what you don't want, click Apply".
        public async Task RefreshAsync()
        {
            if (!UpdaterFound)
            {
                GpuText = $"{Path.GetFileName(UpdaterPath)} not found next to the install - component management unavailable.";
                LoadFailed = true;
                return;
            }

            IsBusy = true;
            StatusLine = "Checking installed components...";
            try
            {
                var (exitCode, output) = await RunUpdaterAsync("--components --json", null);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException(output.Trim());
                }

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement;
                var gpu = root.GetProperty("gpu");
                bool nvidia = gpu.GetProperty("nvidia").GetBoolean();
                GpuText = nvidia
                    ? $"GPU: {gpu.GetProperty("name").GetString()}"
                    : "GPU: no NVIDIA device detected — the built-in DirectML engine covers AMD and Intel GPUs";
                GpuNvidia = nvidia;
                NvidiaGpus = root.TryGetProperty("gpus", out var gpus) && gpus.ValueKind == JsonValueKind.Array
                    ? gpus.EnumerateArray()
                        .Select(g => (Name: g.GetProperty("name").GetString() ?? "",
                                      Uuid: g.GetProperty("uuid").GetString() ?? ""))
                        .Where(g => g.Name.Length > 0 && g.Uuid.Length > 0)
                        .ToList()
                    : [];

                Packs.Clear();
                foreach (var e in root.GetProperty("packs").EnumerateArray())
                {
                    bool installed = e.GetProperty("installed").GetBoolean();
                    bool recommended = e.GetProperty("recommended").GetBoolean();
                    var item = new ComponentItem
                    {
                        Name = e.GetProperty("name").GetString() ?? "",
                        Bytes = e.GetProperty("bytes").GetInt64(),
                        Installed = installed,
                        Recommended = recommended,
                        Preselect = e.TryGetProperty("preselect", out var pre)
                            ? pre.GetBoolean() : installed || recommended,
                    };
                    // Only what this machine uses (recommended), has (installed, so a
                    // full install can be slimmed), or can choose (rife). Kernel packs
                    // for other GPU generations and the TensorRT stack on non-NVIDIA
                    // boxes are irrelevant here; the updater CLI still lists everything.
                    if (!item.Installed && !item.Recommended && !item.Preselect &&
                        item.Name != "rife")
                    {
                        continue;
                    }
                    // checked = currently installed (the checkbox is desired state);
                    // recommended items are highlighted, never pre-checked
                    item.Selected = item.Installed;
                    Packs.Add(item);
                }

                TrtPackAvailable = Packs.Any(p => p.Name == "trt-runtime");
                SetupNeeded = Packs.Any(p => p.Recommended && !p.Installed);
                string? mismatch = root.TryGetProperty("version_mismatch", out var mm)
                    ? mm.GetString() : null;
                StatusLine = mismatch is not null
                    ? mismatch + " Update first, then manage components."
                    : "";
                LoadFailed = false;
            }
            catch (Exception ex)
            {
                GpuText = "Could not load component information.";
                StatusLine = ex.Message;
                LoadFailed = true;
            }
            finally
            {
                IsBusy = false;
                Refreshed?.Invoke();
            }
        }

        // Installs every checked-but-missing pack and removes every unchecked-but-installed
        // one, streaming the updater's progress output into the status line.
        public async void Apply()
        {
            var toInstall = Packs.Where(p => p.Selected && !p.Installed).Select(p => p.Name).ToList();
            var toRemove = Packs.Where(p => !p.Selected && p.Installed).Select(p => p.Name).ToList();
            await RunChangesAsync(toInstall, toRemove);
        }

        // What the first-run dialog offers: the engine's preselect set (hardware
        // recommendations, plus RIFE on installs that never managed components).
        public List<ComponentItem> MissingPreselected =>
            Packs.Where(p => p.Preselect && !p.Installed).ToList();

        public async Task InstallMissingPreselectedAsync()
        {
            await RunChangesAsync(MissingPreselected.Select(p => p.Name).ToList(), []);
        }

        private async Task RunChangesAsync(List<string> toInstall, List<string> toRemove)
        {
            if (IsBusy)
            {
                return;
            }
            if (toInstall.Count == 0 && toRemove.Count == 0)
            {
                StatusLine = "Nothing to change.";
                return;
            }

            IsBusy = true;
            try
            {
                foreach (var name in toInstall)
                {
                    var (exitCode, output) = await RunUpdaterAsync($"--install {name}",
                        line => StatusLine = $"{name}: {line}");
                    if (exitCode != 0)
                    {
                        StatusLine = $"Installing {name} failed: {LastLine(output)}";
                        return;
                    }
                }
                foreach (var name in toRemove)
                {
                    StatusLine = $"Removing {name}...";
                    var (exitCode, output) = await RunUpdaterAsync($"--remove {name}", null);
                    if (exitCode != 0)
                    {
                        StatusLine = $"Removing {name} failed: {LastLine(output)}";
                        return;
                    }
                }
                StatusLine = "Done.";
            }
            finally
            {
                IsBusy = false;
                await RefreshAsync();
            }
        }

        public async void Refresh()
        {
            await RefreshAsync();
        }

        private static string LastLine(string s) =>
            s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
             .LastOrDefault() ?? "unknown error";

        // Runs the updater hidden; onLine (marshalled to the UI thread) sees each output
        // line live, the full output is returned for error reporting.
        private static async Task<(int ExitCode, string Output)> RunUpdaterAsync(
            string arguments, Action<string>? onLine)
        {
            var psi = new ProcessStartInfo
            {
                FileName = UpdaterPath,
                Arguments = arguments,
                WorkingDirectory = MainWindowViewModel.RootDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var process = new Process { StartInfo = psi };
            var output = new System.Text.StringBuilder();
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    return;
                }
                output.AppendLine(e.Data);
                if (onLine is not null && e.Data.Trim() is { Length: > 0 } line)
                {
                    Dispatcher.UIThread.Post(() => onLine(line));
                }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    output.AppendLine(e.Data);
                }
            };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync();
            return (process.ExitCode, output.ToString());
        }
    }
}
