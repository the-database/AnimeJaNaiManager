# Addon Manager review notes

This is the Manager companion to the API 1.12 host/player PR set. The complete reviewer entry point and creator manual are `addons/REVIEW-GUIDE.md` and `addons/DEVELOPER-GUIDE.md` in `mpv-AnimeJaNai`. [ADDONS.md](ADDONS.md) documents local fixture and real-host testing.

The Addons tab connects to the trusted local host automatically. Users install `.ajnaddon` files, review requested permissions, configure typed fields, run declared actions, start/stop addons and restore a previous package. It does not expose a host address, arbitrary guest UI code, shell execution or native DLL installation.

Review `Views/AddonsView.axaml*` for basic state/selection handling and the partial files for media, network, listener, Windows-login, performance-limit and help flows. File/profile/destination approvals are tied to the reviewed package; cancellation and revocation are explicit. Unsaved settings survive unrelated operations and require confirmation before discard. MainWindow retains the 3.9.0 saved UI state and diagnostics shutdown behavior alongside addon lifecycle cleanup.

`Services/AddonHostClient.cs` is an exact normalized copy of the main repo's canonical management client. `addon-sdk-source.json` pins its source and checksum; `tools/check-addon-client.ps1` checks it. The UI CI workflow separately pins the host used for packaged-host integration. Both pins must refer to compatible source; do not update just one during protocol changes.

Tests: configuration/Temporal Fix round trips, Diagnostics, AddonUi fixture tests and the optional real packaged-host path described in ADDONS.md. The 3.9.0 port passed these relevant fixture/configuration checks; the host/player qualification is recorded separately in the main review document. The headless test uses Avalonia 12.1.4 and drains dialog continuations before issuing the next close action.

An unused Addons tab does not start an empty host merely to poll it. Enabled addons can have their own activation rules and work. Manager supplies controls and reviewed access; it does not implement Plex or lighting logic. There is no community catalog or publisher-signature UI in this preview.
