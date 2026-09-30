# AnimeJaNai Manager

Manager for [mpv-upscale-2x_animejanai](https://github.com/the-database/mpv-upscale-2x_animejanai): edit upscaling profiles and chains, and install or remove hardware-specific components (TensorRT runtime, per-GPU kernel packs, RIFE interpolation models) with recommendations based on the detected GPU.

Formerly AnimeJaNaiConfEditor; the executable is `AnimeJaNaiManager.exe` and ships at the install root next to `mpvnet.exe`.

## Experimental automatic display refresh

In **Profiles**, enable **Automatic Display Refresh Rate (Experimental)** and
restart the player. Local files are scanned before playback to choose one
display refresh for the whole episode, including across seeks. Fixed-rate
videos use an exact or near multiple. Mixed-rate videos prefer a common
multiple of all measured cadences; otherwise, AJN minimizes estimated timing
error weighted by each cadence's duration. Unverified inputs use the highest
supported progressive refresh at the current resolution.

Playback pauses with a notice before a needed switch, waits for the display
to settle, and resumes while preserving manual pauses. Matching consecutive
episodes retain their rate. The original refresh is restored when playback
ends or the player closes normally. This is off by default. Scanning can add
startup time, and the TV may briefly go black during a switch. This setting
does not change `video-sync` or guarantee perfect VFR presentation.

This setting requires the companion AJN player with `display-rate-match`
support and the updated `animejanai_backend.lua`. It is saved as
`[global] display_rate_match=yes` in `animejanai/animejanai.conf` and included
in full config exports. It does not change the video frame rate or enable RIFE.

![297521638-76a8db5b-8c67-4b0c-911a-9b02598fb37a](https://github.com/the-database/AnimeJaNaiConfEditor/assets/25811902/e3b5d268-794c-40d3-b0ae-2d781d98d6ae)
