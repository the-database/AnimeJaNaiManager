# Addon Manager developer preview

Opening Addons automatically starts or connects to the local companion service in `mpv-AnimeJaNai/addons`. The API 1.12 integration is based on AJN 3.9.0. See [ADDON-API-REVIEW.md](ADDON-API-REVIEW.md) for the review map and matching host dependency. End users do not configure a host address or connection.

The assembler puts the host at `<AJN root>/addon-host/ajn-addon.exe`, the launcher beside it, and pinned Wasmtime at `addon-host/runtime/wasmtime.exe`. The service starts on demand without a console. Development tests can set `ANIMEJANAI_ROOT` and `ANIMEJANAI_DATA_DIR`; data lives under `<data directory>/addons`.

Choose Install local addon and select an `.ajnaddon` package. Review its identity and check only the permissions you want to grant. All checkboxes start unchecked. Installation is bound to the reviewed archive's hash. Local development packages do not establish publisher identity; website/catalog installation is later work.

The page provides status, start/stop, previous-version rollback, removal, typed settings, declared actions and recent logs. Settings are validated by the host. Incompatible saved fields show a repair notice and defaults for review; reading the form does not overwrite them. Remove stops the addon but preserves its data. Manual starts persist after Manager closes; activation that belongs only to Manager ends when its last relevant connection closes.

API 1.7 adds **Listening access** for addons that request `network.listen`.
Start with local access unless the addon needs another device to connect.
LAN/public access requires a separate reviewed binding, port and accepted host
names. HTTPS certificates are imported into the host with a hostname/expiry
check; their private keys are not sent to the addon. Import a replacement
certificate, choose it for the listener and restart the addon when rotating
certificates. Removing access stops the addon before removing its approval.
An entered public URL is shown as unverified: Manager does not configure the
router, firewall, DNS or internet reachability.

Listener access, upstream service access, proxying and temporary client
credentials are separate permissions. Approving a listener alone does not
authorize remote media access or processing. The matching runtime's
`HTTP-SERVER.md` and `STREAMING.md` describe resource approval and public APIs;
the full standalone developer guide includes the authoring contracts.

Addon labels/descriptions/settings are rendered with trusted native controls. Addon HTML, assemblies or external configuration executables are never loaded. The management protocol is separate from the restricted guest broker. `Services/AddonHostClient.cs` is a verbatim copy of the host's `sdk/csharp/ManagementClient.cs`.

The preview includes approved native media sessions, DirectML/D3D11 processed SDR samples, normal-player samples, encoded output, network destinations and optional credentials when the installed runtime advertises those capabilities. NVENC also requires supported NVIDIA hardware. CUDA/TensorRT samples, HDR, final display composition, website/catalog installation and the example Plex/bias-light applications remain separate work. Windows is implemented first; Linux assembly excludes this runtime.

Updates block activation and drain workers before replacement. Interrupted updates keep a recovery journal and pause addons; close the player and Manager and run the installer again to recover. Remove addon preserves private data. Full uninstall follows the existing app-tree deletion policy and preserves external data roots and login entries now owned by another installation.

`addon-sdk-source.json` pins the canonical management client and checksum. Run `./tools/check-addon-client.ps1` before publishing; CI compares the copied client with that exact source before packaged-host checks.

## Verify the controls

From this repository in PowerShell 7 with .NET SDK 10:

```powershell
dotnet run --project ./tests/AddonUi/AddonUi.csproj -c Release '-p:UsedAvaloniaProducts=' -- ./addon-ui-results
```

The headless Avalonia test opens no desktop windows. It checks settings through save/reload, actions, start/stop, explicit permissions, and package-hash binding. It writes `results.json`, `addon-manager.png` and `addon-permissions.png`. The MSBuild property disables the dependency's usage-telemetry task during testing; it does not disable compilation or validation. The private pipe requires a normal same-user Windows token.

Pass an integrated preview root as a second argument to test the actual packaged host instead of the UI fixture. That root needs `addon-host/ajn-addon.exe`, `addon-host/runtime/wasmtime.exe` and `addon-development/counter.ajnaddon`. This path starts the host from Manager, installs the example with reviewed permissions, changes settings, closes/reopens the page with a manual activation, invokes real Wasm actions, checks durable storage, removes the addon and verifies clean idle exit. It uses a new isolated data directory under the test output and captures `addon-real-host.png`. CI builds the pinned matching host and runs both scenarios.
