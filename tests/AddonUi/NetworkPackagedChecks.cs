using AnimeJaNai.Addons.Management;
using AnimeJaNaiConfEditor.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class NetworkPackagedChecks
{
    public static async Task RunAsync(AddonsView view, Window window, string root, string output)
    {
        const string id = "org.animejanai.service-inspector";
        T Find<T>(string name) where T : Control => view.FindControl<T>(name)!;
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        void Check(bool ok, string message) { if (!ok) throw new Exception(message + " " + Find<TextBlock>("Status").Text); }
        async Task Until(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!ready())
            {
                if (timeout.IsCancellationRequested) throw new TimeoutException("Network UI state: " + Find<TextBlock>("AddonState").Text + "; status: " + Find<TextBlock>("Status").Text);
                await Task.Delay(25);
            }
        }
        async Task Accept(Task operation, string label, Action<Window>? edit = null)
        {
            await Until(() => window.OwnedWindows.Count > 0);
            var dialog = window.OwnedWindows.Single(); edit?.Invoke(dialog);
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await operation;
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource();
        string origin = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var serve = Task.Run(async () =>
        {
            try
            {
                using var accepted = await listener.AcceptTcpClientAsync(stop.Token);
                var stream = accepted.GetStream(); using var header = new MemoryStream(); byte[] next = new byte[1];
                while (header.Length < 16384)
                {
                    if (await stream.ReadAsync(next, stop.Token) == 0) throw new IOException("Request ended early.");
                    header.WriteByte(next[0]);
                    string value = Encoding.ASCII.GetString(header.ToArray());
                    if (value.EndsWith("\r\n\r\n", StringComparison.Ordinal)) { received.TrySetResult(value); break; }
                }
                await stream.WriteAsync("HTTP/1.1 200 OK\r\nContent-Length: 3\r\nContent-Type: application/octet-stream\r\nConnection: close\r\n\r\n"u8.ToArray(), stop.Token);
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, stop.Token);
            }
            catch (OperationCanceledException) { }
        });
        try
        {
            await Accept(view.ReviewPackageAsync(Path.Combine(root, "addon-development", "service-inspector.ajnaddon"), window), "Install", dialog =>
            {
                foreach (var box in dialog.GetVisualDescendants().OfType<CheckBox>()) box.IsChecked = true;
            });
            Click("RefreshButton"); await Until(() => Find<Button>("RefreshButton").IsEnabled);
            Check(Find<StackPanel>("NetworkSection").IsVisible, "Missing real service capability.");
            await using var client = await ManagementClient.ConnectAsync(Path.Combine(output, "data", "addons"));
            string hash = (await client.ListAsync()).Single(item => item!["id"]!.GetValue<string>() == id)!["hash"]!.GetValue<string>();
            await Accept(view.ApproveDestinationAsync(window, id, hash, "Loopback test service", origin), "Allow service", dialog =>
            {
                using var image = dialog.CaptureRenderedFrame(); image!.Save(Path.Combine(output, "addon-real-service-consent.png"));
            });
            var selected = (JsonObject)((JsonArray)(await client.CallAsync("network.selections", new() { ["id"] = id }))!["destinations"]!)[0]!;
            Check(selected["origin"]!.GetValue<string>() == origin, "Approved the wrong service.");
            await Accept(view.SetNetworkCredentialAsync(window, id, hash, selected), "Save credential", dialog =>
            {
                dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CredentialHeader").Text = "X-Test-Credential";
                dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "CredentialValue").Text = "synthetic-integration-value";
            });
            Find<StackPanel>("SettingFields").GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            Click("SaveButton"); await Until(() => Find<Button>("SaveButton").IsEnabled);
            Click("StartButton"); await Until(() => Find<TextBlock>("AddonState").Text?.EndsWith("\nRunning", StringComparison.Ordinal) == true && Find<Button>("StartButton").IsEnabled);
            Find<StackPanel>("ActionFields").GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Send request").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            string headers = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(headers.Contains("X-Test-Credential: synthetic-integration-value\r\n", StringComparison.OrdinalIgnoreCase), "Saved credential did not reach the approved service.");
            JsonNode? response = null;
            for (int attempt = 0; attempt < 100; attempt++)
            {
                response = await client.CallAsync("addons.action", new() { ["id"] = id, ["action"] = "status" });
                if (response?["state"]?.GetValue<string>() == "completed") break;
                await Task.Delay(30);
            }
            Check(response?["status"]?.GetValue<int>() == 200 && response?["bytes"]?.GetValue<int>() == 3, "Actual Wasm did not receive HTTP bytes.");
            Find<StackPanel>("NetworkFields").GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Remove credential").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Find<TextBlock>("AddonState").Text?.EndsWith("\nStopped", StringComparison.Ordinal) == true && Find<Button>("RefreshButton").IsEnabled);
            var after = (JsonArray)(await client.CallAsync("network.selections", new() { ["id"] = id }))!["destinations"]!;
            Check(after.Count == 1 && after[0]!["hasCredential"]!.GetValue<bool>() == false, "Credential removal changed the destination.");
            Find<StackPanel>("NetworkFields").GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Remove access").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => !Find<StackPanel>("NetworkFields").GetVisualDescendants().OfType<Button>().Any() && Find<Button>("RefreshButton").IsEnabled);
            Check(((JsonArray)(await client.CallAsync("network.selections", new() { ["id"] = id }))!["destinations"]!).Count == 0, "Access remains after removal.");
            await client.CallAsync("addons.remove", new() { ["id"] = id });
            File.WriteAllText(Path.Combine(output, "network-results.json"), JsonSerializer.Serialize(new { passed = true, checks = new[] {
                "packaged service capability", "real package permission review", "pinned destination consent", "masked scoped credential save",
                "saved settings", "actual Wasm HTTP request", "binary HTTP response", "credential removal stops addon", "credential removal preserves destination", "destination and addon cleanup" } }));
            Console.WriteLine("PASS packaged Manager -> host -> Wasm -> approved service, scoped credential and revocation.");
        }
        finally { stop.Cancel(); listener.Stop(); await serve; }
    }
}
