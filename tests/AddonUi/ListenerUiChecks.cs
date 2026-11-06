using AnimeJaNaiConfEditor.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;

internal static class ListenerUiChecks
{
    public static async Task RunAsync(AddonsView view, Window window, FixtureServer server, string output)
    {
        T Find<T>(string name) where T : Control => view.FindControl<T>(name)!;
        void Check(bool value, string text) { if (!value) throw new Exception(text); }
        async Task Until(Func<bool> ready)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!ready()) await Task.Delay(20, deadline.Token);
        }
        Check(!Find<StackPanel>("ListenerSection").IsVisible, "Listener controls appeared without permission.");
        server.ListenerPermission = true;
        Find<Button>("RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Find<StackPanel>("ListenerSection").IsVisible && Find<Button>("RefreshButton").IsEnabled);
        const string id = "org.example.ui"; string hash = new('a', 64);
        var review = view.ApproveListenerAsync(window, id, hash, "Local test", 7888, new JsonArray("X-Key"), new JsonArray("token"));
        await Until(() => window.OwnedWindows.Count > 0);
        var dialog = window.OwnedWindows.Single();
        Check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text?.Contains("127.0.0.1:7888") == true), "Exact local listener endpoint missing from consent.");
        using (var image = dialog.CaptureRenderedFrame()) image!.Save(Path.Combine(output, "addon-listener-consent.png"));
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await review; Check(server.Listeners.Count == 0, "Cancellation approved a listener.");
        review = view.ApproveListenerAsync(window, id, hash, "Local test", 7888, [], []);
        await Until(() => window.OwnedWindows.Count > 0);
        window.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Allow local listener").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await review;
        Check(server.Listeners.Count == 1 && server.ListenerReviewHash == hash, "Listener consent lost the package hash.");
        Find<StackPanel>("ListenerSection").BringIntoView();
        await Task.Delay(100);
        using (var image = window.CaptureRenderedFrame()) image!.Save(Path.Combine(output, "addon-listener-controls.png"));
        Find<StackPanel>("ListenerFields").GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Remove access").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => server.Listeners.Count == 0 && Find<Button>("RefreshButton").IsEnabled);
        Check(Find<StackPanel>("ListenerFields").GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Import HTTPS certificate"), "Certificate import control missing.");
        review = view.ApproveListenerAsync(window, id, hash, "Remote clients", 8443, [], [], new JsonObject
        {
            ["address"] = "0.0.0.0", ["scheme"] = "https", ["scope"] = "public", ["certificateId"] = "fixture", ["certificateHost"] = "bridge.example",
            ["allowedHosts"] = new JsonArray("bridge.example"), ["publicBaseUrl"] = "https://bridge.example:8443",
            ["cors"] = new JsonObject { ["origins"] = new JsonArray("https://client.example"), ["methods"] = new JsonArray("GET", "HEAD"), ["headers"] = new JsonArray("X-Client"), ["exposeHeaders"] = new JsonArray("ETag"), ["allowCredentials"] = true }
        });
        await Until(() => window.OwnedWindows.Count > 0);
        dialog = window.OwnedWindows.Single();
        string text = string.Join("\n", dialog.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));
        Check(text.Contains("HTTPS at 0.0.0.0:8443") && text.Contains("public") && text.Contains("unverified") && text.Contains("https://client.example") && text.Contains("X-Client"), "Network listener review omitted endpoint, reachability or browser access.");
        using (var image = dialog.CaptureRenderedFrame()) image!.Save(Path.Combine(output, "addon-network-listener-consent.png"));
        dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await review; Check(server.Listeners.Count == 0, "Network listener cancellation granted access.");
    }
}
