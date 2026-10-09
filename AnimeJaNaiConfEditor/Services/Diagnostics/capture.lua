-- Diagnostic metadata only. No pixel readback, screenshots, or media uploads.
local mp = require 'mp'
local utils = require 'mp.utils'
local destination = os.getenv('AJN_DIAGNOSTIC_TIMELINE')
if not destination then return end
local properties = {
    'mpv-version', 'ffmpeg-version', 'libass-version', 'current-vo', 'gpu-api',
    'gpu-context', 'hwdec', 'hwdec-current', 'video-sync', 'display-fps',
    'estimated-display-fps', 'container-fps', 'estimated-vf-fps', 'speed',
    'time-pos', 'duration', 'pause', 'seeking', 'idle-active', 'vo-configured',
    'video-codec', 'video-format', 'video-params', 'video-out-params',
    'video-target-params', 'vf', 'af', 'audio-codec-name', 'audio-params',
    'audio-out-params', 'avsync', 'decoder-frame-drop-count', 'frame-drop-count',
    'mistimed-frame-count', 'vo-delayed-frame-count', 'cache-buffering-state',
    'demuxer-cache-duration', 'sub-ass-override', 'sub-render-ahead-frames',
    'sub-hdr-peak', 'hdr-reference-white', 'target-colorspace-hint',
    'target-prim', 'target-trc', 'target-peak', 'inverse-tone-mapping',
    'interpolation', 'fullscreen', 'window-scale', 'osd-dimensions',
    'vulkan-queue-count', 'sid', 'aid', 'vid', 'user-data/animejanai'
}
local function snapshot(event)
    local record = { utc = os.date('!%Y-%m-%dT%H:%M:%SZ'),
        monotonic_seconds = mp.get_time(), event = event, properties = {} }
    for _, name in ipairs(properties) do
        record.properties[name] = mp.get_property_native(name)
    end
    if event == 'file-loaded' or event == 'startup' then
        record.bindings = mp.get_property_native('input-bindings')
    end
    local file = io.open(destination, 'a')
    if file then file:write(utils.format_json(record), '\n'); file:close() end
end
for _, event in ipairs({'file-loaded', 'seek', 'playback-restart', 'end-file', 'shutdown'}) do
    mp.register_event(event, function() snapshot(event) end)
end
mp.add_periodic_timer(1, function() snapshot('sample') end)
snapshot('startup')
