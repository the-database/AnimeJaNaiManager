using System;
using System.IO;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Services.PlayerSettings;

// A running player's JSON IPC (mpv's --input-ipc-server), given to the Manager as
// --player-ipc <endpoint> when the player opens it: a Windows named pipe (\\.\pipe\<name>) or a
// Unix socket path. Changes are sent with mpv's `set` command after the file is written.
public static class PlayerIpc
{
    private static string? _endpoint;
    public static event Action? EndpointChanged;

    public static string? Endpoint
    {
        get => _endpoint;
        set
        {
            value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (value == _endpoint) return;
            _endpoint = value;
            EndpointChanged?.Invoke();
        }
    }

    public static string? PipeName(string endpoint)
    {
        foreach (string prefix in new[] { @"\\.\pipe\", "//./pipe/" })
            if (endpoint.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return endpoint[prefix.Length..];
        return null;
    }

    // What SetAsync returns when nothing answers at the endpoint: the player was closed.
    public const string NotRunning = "the player isn't running";

    // null when the player took the value, else why not (NotRunning when it's gone).
    public static async Task<string?> SetAsync(string endpoint, string option, string value, TimeSpan? timeout = null)
    {
        Stream stream;
        using (var connect = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            try { stream = await ConnectAsync(endpoint, connect.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or TimeoutException or UnauthorizedAccessException)
            { return NotRunning; }
        }
        using var cancel = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            await using var _ = stream;
            int id = Random.Shared.Next(1, int.MaxValue);
            byte[] request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { command = new[] { "set", option, value }, request_id = id }) + "\n");
            await stream.WriteAsync(request, cancel.Token);
            await stream.FlushAsync(cancel.Token);
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            while (await reader.ReadLineAsync(cancel.Token) is { } line)
            {
                using var reply = JsonDocument.Parse(line);
                var root = reply.RootElement;
                if (!root.TryGetProperty("request_id", out var rid) || rid.ValueKind != JsonValueKind.Number || rid.GetInt32() != id) continue;
                string error = root.TryGetProperty("error", out var e) ? e.GetString() ?? "" : "";
                return error == "success" ? null : error;
            }
            return "the player closed the connection";
        }
        catch (OperationCanceledException) { return "the player didn't answer"; }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or UnauthorizedAccessException or JsonException)
        { return ex.Message; }
    }

    private static async Task<Stream> ConnectAsync(string endpoint, CancellationToken cancel)
    {
        if (PipeName(endpoint) is { } pipe)
        {
            var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous);
            try { await client.ConnectAsync(cancel); return client; }
            catch { await client.DisposeAsync(); throw; }
        }
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), cancel); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }
}
