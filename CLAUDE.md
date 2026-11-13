# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

AnimeJaNaiConfEditor is a Windows desktop GUI for configuring AnimeJaNai, an AI-powered anime video upscaling tool. It manages upscaling profiles, processing chains, and AI model settings that get written to an INI-style config file consumed by mpv/AnimeJaNai.

## Build Commands

```bash
dotnet build                     # Debug build
dotnet build -c Release          # Release build
dotnet publish -c Release        # Publish self-contained win-x64 executable
```

No linter is configured. `dotnet run --project tests/ProfileRoundTrip` is a console regression
test for conf/profile serialization (it compiles the production view-model files); it exits
nonzero on failure. `dotnet run --project tests/RuntimePacks` checks where the "TensorRT installed"
check looks: the pack store's TensorRT pack that `animejanai/inference/aji-runtime.json` names
(written by the updater's store mode), else the inference folder. `dotnet run --project tests/Storage`
covers Components > Storage: reading the updater's `--storage --json` (and the fallback to
`aji-runtime location --json`), the "a folder I choose" rule, when the section shows, which option is
current, switching through `--storage <choice>`, and the unplugged-drive handling. The test exe plays
the updater and aji-runtime itself (`tests/Storage/FakeTools.cs`, through
`StoreLocation.UpdaterOverride` / `AjiRuntimeOverride`); set `AJI_RUNTIME_EXE` to an aji-runtime
0.2.0+ build to also drive the real tool on a temporary store.
`dotnet run --project tests/TensorRtGpus` checks which NVIDIA GPUs count for TensorRT
(`Services/TensorRtGpus.cs`): it needs compute capability 7.5+ (GeForce RTX 20 / GTX 16 series and
newer, `TrtMinComputeCapability`). An older GPU counts as no NVIDIA GPU, so TensorRT isn't
selectable and the backend falls back to DirectML; the updater reports such GPUs in
`unsupported_gpus` (and leaves them out of `gpu.nvidia`, `gpu.sm` and `gpus`), and for an updater
from before that the Manager applies the rule to `gpu.sm` itself.

## Architecture

**Framework:** Avalonia UI (.NET 10.0) with ReactiveUI (MVVM pattern).

**Data model hierarchy:** `AnimeJaNaiConf` → `UpscaleSlot[]` → `UpscaleChain[]` → `UpscaleModel[]`. All nested in `MainWindowViewModel.cs`, which is the central file (~1400 lines) containing both the ViewModel and all model classes.

**Auto-save:** Model properties use `RaiseAndSetIfChanged` and subscribe via Rx to trigger `WriteAnimeJaNaiConf()` on any change. Modifying model properties automatically persists to the config file.

**View binding:** `ViewLocator.cs` maps ViewModels to Views by convention (replacing "ViewModel" suffix with "View"). Currently there is only one view: `MainWindow`.

**Config persistence:** Uses `Salaros.ConfigParser` to read/write `animejanai.conf` in INI format. Sections are `[global]` and `[slot_N]`, with chain/model properties using `chain_N_model_N_` key prefixes.

**Player settings:** Some `[global]` keys configure mpv rather than the inference engine — `default_slot` and `sub_render_mode` (the GPU subtitle toggle). The engine ignores them; `portable_config/scripts/animejanai_backend.lua` in the mpv-upscale-2x_animejanai package reads them at startup. The mpv options themselves live in a managed profile (`[subs-gpu]` in `mpv-animejanai.conf`), so the settings can change with the player build without a Manager release. This app never writes `mpv.conf`. The GPU choice works the same way: `gpu_tensorrt` (a Vulkan/CUDA device UUID, from the updater's `gpus` list) and `gpu_directml` (a D3D11 adapter's PCI slot, `pci:<bus>:<device>.<function>` in hex, or its name when Windows reports no slot; from `Services/GpuAdapters.cs`: DXGI in high-performance order without indirect-display adapters, slots via D3DKMT, integrated GPUs by Direct3D 12 UMA; the `pci:` form needs the mpv fork's `--d3d11-adapter=pci:...`), written only when a GPU is chosen; the Lua maps them to `vulkan-device` / `d3d11-adapter`.

**Chain feature keys (need aji v0.17.0+, which ships in the same mpv package):**
- `chain_N_upscale=no`: the engine skips the chain's models; they stay under `chain_N_model_M_*` so turning upscaling back on restores them.
- `chain_N_temporal_fix_model` + `chain_N_temporal_fix_scene_threshold`: the engine runs that vs_temporalfix model after the upscale models (or alone). The Manager picks the file, the newest `temporalfix_s<k>_v*` in `onnx/`, and reads the strength back from the name.
- Both are written only while the feature differs from its default, so other confs are unchanged.

**UI state:** the Manager's own layout state (sidebar width, which collapsible sections are open) lives in `manager-ui.json` next to `animejanai.conf` (`Services/UiState.cs`), never in the conf. Section keys are `profile<N>:chain<M>:<chain|activation|upscale|tfix|rife>`, `default<N>:...` and `global:trt`. The mpv package lists the file in `user_preserve`.

**Layout:** profile chains (and their Activation Condition, Upscaling, Temporal Fix and RIFE blocks) and the TensorRT engine settings are `Expander.section`s with a one-line summary in the header (`ChainSummary`, `TemporalFixSummary`, `RifeSummary`, `TrtSummary`). Setting help is a `ContentControl.hint` (label + (?) icon, explanation in `ToolTip.Tip`), not visible text. The profiles sidebar is resizable (`GridSplitter`) and collapses to icons below 120 px (`compact` class).

**Storage (Components > Storage, `Services/StoreLocation.cs`):** where this copy keeps the TensorRT
runtime and its GPU kernels (its pack store). The updater (run in the install root) owns the choice:
`AnimeJaNaiUpdater --storage --json` prints `{"choice": "inside"|"shared"|"custom", "root", "moved",
"target", "available", "inside", "shared", "installed", "bytes"}` (`bytes`: this copy's TensorRT
packs, what a switch moves; the confirmation falls back to the folder's size without it), and `--storage inside|shared|<folder>`
switches and moves the packs (`AJI_MOVE <copied> <total>` progress lines; on failure a non-zero exit
with the reason as the last output line). The section offers the three options (inside this install,
shared with the other JaNai apps, a folder the user picks: a folder that already is a store, with
`store.json`, is used as is so apps share it; a new or empty folder as is; else
`<folder>\JaNai shared runtime`). Move... / Move Back to Default / Try Again / Reset to Default
(`aji-runtime move/move --default/reset`) move the shared folder itself and are enabled only while
this copy uses it. An updater without `--storage` (non-zero exit or other output) gets the old
section: only the shared folder, through `aji-runtime location --json`, and only once TensorRT is in it
(`inference/aji-runtime.json`). The section shows when aji-runtime is at the install root, the store
could be read, and TensorRT is installed here or the PC has a TensorRT-capable NVIDIA GPU
(`StoreLocation.Shows`); hidden for dev builds and a Manager without the updater. While the folder is
unavailable (`available: false`) and this copy has TensorRT in it, TensorRT only looks missing: the
backend stays, the setup banner gives way to the missing-folder banner (`StorageMissing`). Storage is
read after the component refresh (the updater's first run picks this copy's default) and after
every component refresh.

**Relative paths from executable:** The app resolves paths relative to its own location — `./animejanai.conf`, `./onnx/`, `./backups/`, and `../portable_config/mpv.conf` for mpv integration.

## Key Conventions

- Decimal formatting uses `CultureInfo.InvariantCulture` to handle non-English Windows locales
- Default profiles are read-only; custom profiles (slots 4+) are editable
- Backup system auto-creates up to 10 backups in `./backups/`, deduplicating by content
- Pre-packaged ONNX neural network models live in `./onnx/`

## Repo and directory naming

The GitHub repo is **`the-database/AnimeJaNaiManager`**, but the project directory and the
csproj/solution keep the old `AnimeJaNaiConfEditor` name. Only
`<AssemblyName>AnimeJaNaiManager</AssemblyName>` reflects the rename, so the built binary
is `AnimeJaNaiManager.exe`. Do not rename paths to "fix" this.

## Releasing

`.github/workflows/release.yml`, two independent legs:

```bash
gh workflow run Release -R the-database/AnimeJaNaiManager -f release_version=0.5.0
```

- **Windows leg** — gated on `if: github.event_name == 'workflow_dispatch'`, so it runs **only**
  on a manual dispatch. Publishes self-contained single-file `win-x64`, stages only the exe plus
  the native Avalonia DLLs, and uploads `AnimeJaNaiManager-portable-x64.zip`.
- **Linux leg** — runs on dispatch *and* on a push to `linux-support` touching
  `AnimeJaNaiConfEditor/**` or the workflow. Publishes `linux-x64` and uploads
  `AnimeJaNaiManager-portable-linux-x64.tar.zst`.

Both stage **only** the binary and its native libs. `animejanai.conf`, `onnx/`, and `*.pdb` are
deliberately excluded: those are owned by the `mpv-upscale-2x_animejanai` overlay and must not
ship in this artifact. `dotnet publish` is invoked directly rather than through
`FolderProfile.pubxml`, which sets `SelfContained=false`.

> **The release must stay published, not draft.** The workflow sets `draft: false` with the
> comment: "Published (not draft) so the mpv build can fetch the asset over the public
> `releases/download/<tag>/` URL - draft assets 404 for unauthenticated requests." The
> AnimeJaNai assembler downloads this asset unauthenticated in `InstallAnimeJaNaiManager()`, so
> a draft release silently breaks the package build.

`env.DEFAULT_TAG` (currently `0.4.0`) is the fallback tag for push-triggered runs; always pass
`release_version` on a real release.

After publishing, bump **`ManagerVersion`** in `BuildMpvUpscale2xAnimeJaNai/Program.cs` in
`the-database/mpv-AnimeJaNai` — that constant is what pulls this build into the package. A
dispatched release does it for you: its last job (`notify-downstream.yml`, needs the
`JANAI_BUMP_TOKEN` secret) has mpv-AnimeJaNai open the bump PR. The
release runbook for the whole project is that repo's `docs/RELEASE.md`.

`setup-dotnet` pins `dotnet-version: '10.x'`, which must match `<TargetFramework>net10.0</TargetFramework>`.
