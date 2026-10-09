# Sending an AnimeJaNai diagnostic report

Open **AnimeJaNai Manager → Troubleshooting**. You do not need to send your
installation, videos, model files, or TensorRT engines.

1. Optionally describe the problem and choose a video. Leave the video empty if
   the player will not open.
2. Click **Record a problem**. Reproduce it in the new player. For an intermittent
   problem, click **Mark problem now** while it is happening; you can mark more
   than one moment and edit the notes between marks. Each click asks the player
   for its current video position and adds a numbered timestamp on a new line,
   including after seeking. If the player cannot respond or no video is loaded,
   the marker says **Playback time unavailable** instead of showing clock time.
3. Click **Finish and save report**. The test player closes and Manager creates
   a ZIP. Use **Open report folder** and attach that ZIP to your bug report.

Recording also finishes when the test player closes or after five minutes. A
player that never opens can still be diagnosed: wait about 20 seconds, then
finish. Closing Manager during recording finishes the recording and saves the
report before Manager closes. Other player instances are not stopped.

If you cannot reproduce the problem, **Save diagnostics without recording**
collects the existing logs, settings and system details. Try this before clearing
engines, resetting settings or reinstalling. These actions may erase evidence.

Reports are saved in **Documents/AnimeJaNai Diagnostics** by default. Use
**Change report folder** to choose another destination. If saving fails, change
the destination and click **Retry saving report**; the recording is retained.

## What the ZIP contains

| File or folder | Purpose |
| --- | --- |
| `report.json` | Report ID, UTC timestamps, user notes, included files, warnings and truncation notices |
| `system.json` | OS/Manager version, CPU/GPU/driver information, GPU memory/utilization snapshot, active display resolution and fractional refresh, Windows HDR state, Vulkan layer registrations and selected graphics environment variables |
| `inventory.json` | Installed component/script/model/engine names, sizes and dates, executable versions and hashes where practical, and small ONNX models' input dimensions |
| `settings/` | Current AJN and portable mpv configuration files |
| `settings-at-start/` | Settings before recording, useful when presets change during reproduction |
| `recent-config-backups/` | Three newest AJN configuration backups |
| `logs/` | Existing AJN/benchmark logs, recognized player logs, and Manager exceptions |
| `engine-logs/` | Six newest TensorRT build logs per model directory |
| `recording/player.log` | Verbose mpv log from the diagnostic instance |
| `recording/vulkan-loader.log` | Diagnostic instance's stderr, including Vulkan loader/layer messages |
| `recording/timeline.jsonl` | UTC/playback times, renderer, decoder, frame-drop/sync counters, video/audio formats, subtitle settings and selected options. Samples once per second and at playback/seek events. Initial bindings help diagnose keyboard/layout issues. |
| `recording/process.jsonl` and `modules.json` | Process memory/CPU/window state and observed loaded DLLs, including overlays, even when player initialization hangs |
| `recording/markers.jsonl` | Numbered problem markers with video position in seconds and formatted time, UTC click time, and the user's current notes |
| `recording/result.json` | Recording/exit result, whether forced shutdown was necessary, and temporary launch overrides |
| `previous-recording/` | Most recent interrupted recording, when one exists; use its own timestamps rather than assuming it describes the current run |

Check `report.json` for missing or shortened data. System/HDR information is a
snapshot at export time; the playback timeline records display FPS during the run.
Missing or unsupported properties are omitted, not interpreted as zero or disabled.
If startup hangs before the diagnostic script can load, the report explains the
missing timeline and still includes startup/process evidence.

Files larger than 2 MiB retain their beginning and end. Redirected console output
rotates at 2 MiB. Recording stops if the player log exceeds 32 MiB. ZIP text is
limited to 32 MiB before compression, with fresh recording data prioritized.
Completed raw recordings are removed after successful export. An interrupted
recording remains under the user's local application-data directory and the
newest one is recovered into the next report.

## Privacy and recording behavior

Reports are saved locally; this feature does not upload them. Video/model/engine
binaries, screenshots, memory dumps, playback history/watch-later files, and the
full environment are excluded. Linked configuration files/directories are not
followed. User/install folders and common credential fields, authorization
headers, URL credentials and URL queries are masked throughout exported text.
This is not a guarantee of anonymity: file names, free-form notes and unusual
custom credentials can remain. Open and review the ZIP before posting publicly.

The recorder keeps the existing renderer, backend, profiles and user scripts.
It requests a separate player instance, extra logging, a private control pipe
and a metadata-only Lua probe. It does not copy video frames to the CPU, toggle
HDR, disable Vulkan layers or change saved settings. Normal player behavior,
such as saving playback position or building an engine, still applies. Logging
adds some overhead, so report whether a performance problem also occurs normally.

Live recording is available on Windows with mpv.net or mpv. Other platforms can
save a snapshot, but Windows-specific hardware information is unavailable. A report is diagnostic
evidence, not an automatic determination of the cause.

## Maintainer verification

`tests/Diagnostics` exercises redaction/export, log truncation, settings
snapshots, malformed model metadata, private IPC shutdown and a simulated hung
player while another player remains alive. Run its built apphost with an output
directory. Its optional `--live <isolated-install> [video]` mode records a
12-second real-player session and saves the resulting report.
