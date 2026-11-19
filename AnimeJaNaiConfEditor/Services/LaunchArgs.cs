using System;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AnimeJaNaiConfEditor.Services
{
    // What the Manager was started with. The player passes these when it opens the Manager:
    //   --page <name>              open on that tab (profiles, player, components, addons, troubleshooting)
    //   --player-ipc <endpoint>    the player's IPC server, for applying player settings live
    //   --owner-window <window-id> (Windows) the player window, which then owns the Manager's
    // Both "--name value" and "--name=value" work. Anything else is ignored, so an older or newer
    // player can pass arguments this Manager doesn't know.
    public sealed record LaunchArgs(string? Page = null, string? PlayerIpc = null, long? OwnerWindow = null)
    {
        public static readonly LaunchArgs None = new();

        public bool IsEmpty => Page is null && PlayerIpc is null && OwnerWindow is null;

        public static LaunchArgs Parse(string[] args)
        {
            string? page = null, ipc = null;
            long? owner = null;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal)) continue;
                string name = arg[2..];
                string? value = null;
                int eq = name.IndexOf('=');
                if (eq >= 0) { value = name[(eq + 1)..]; name = name[..eq]; }
                if (name is not ("page" or "player-ipc" or "owner-window")) continue;
                if (value is null)
                {
                    if (i + 1 >= args.Length || args[i + 1].StartsWith("--", StringComparison.Ordinal)) continue;
                    value = args[++i];
                }
                switch (name)
                {
                    case "page": page = NormalizePage(value); break;
                    case "player-ipc": ipc = string.IsNullOrWhiteSpace(value) ? null : value; break;
                    case "owner-window": owner = ParseWindowId(value); break;
                }
            }
            return new LaunchArgs(page, ipc, owner);
        }

        public static string? NormalizePage(string? page) =>
            string.IsNullOrWhiteSpace(page) ? null : page.Trim().ToLowerInvariant();

        // mpv's window-id is a decimal number; accept 0x-hex too.
        public static long? ParseWindowId(string? text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text)) return null;
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return long.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long hex) && hex != 0 ? hex : null;
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id) && id != 0 ? id : null;
        }

        // Activation pipe message from a second launch: the byte 1 (all a Manager before this
        // reads, so it still just comes to the front), then these arguments as UTF-8 JSON.
        public byte[] ToActivationMessage()
        {
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new { page = Page, player_ipc = PlayerIpc, owner_window = OwnerWindow });
            var message = new byte[json.Length + 1];
            message[0] = 1;
            json.CopyTo(message, 1);
            return message;
        }

        // The part after the first byte; empty (an older launch) or unreadable means none.
        public static LaunchArgs FromActivationPayload(ReadOnlySpan<byte> payload)
        {
            if (payload.IsEmpty) return None;
            try
            {
                using var document = JsonDocument.Parse(Encoding.UTF8.GetString(payload));
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return None;
                string? Str(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                long? owner = root.TryGetProperty("owner_window", out var o) && o.ValueKind == JsonValueKind.Number && o.TryGetInt64(out long id) && id != 0 ? id : null;
                return new LaunchArgs(NormalizePage(Str("page")), Str("player_ipc") is { Length: > 0 } ipc ? ipc : null, owner);
            }
            catch (JsonException) { return None; }
        }
    }
}
