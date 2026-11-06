using AnimeJaNai.Addons.Management;
using AnimeJaNaiConfEditor.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Text.Json;

internal static class HostSettingsPackagedChecks
{
    public static async Task RunAsync(AddonsView view, Window owner, string output)
    {
        var button = view.FindControl<Button>("HostSettingsButton")!;
        // Older packaged hosts remain supported without this optional editor.
        if (!button.IsVisible) return;
        await using var client = await ManagementClient.ConnectAsync(Path.Combine(output, "data", "addons"));
        int original = (await client.CallAsync("host.settings"))!["maximumConcurrentSessions"]!.GetValue<int>();
        async Task Until(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!ready()) await Task.Delay(20, timeout.Token);
        }
        async Task<Window> Open()
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => owner.OwnedWindows.Count == 1); return owner.OwnedWindows.Single();
        }
        async Task Save(Window dialog, int count)
        {
            dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = count.ToString();
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Save").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => owner.OwnedWindows.Count == 0 && button.IsEnabled);
            if ((await client.CallAsync("host.settings"))!["maximumConcurrentSessions"]!.GetValue<int>() != count) throw new Exception("Packaged host did not retain the chosen limit.");
        }
        try
        {
            var dialog = await Open(); await Save(dialog, 3);
            dialog = await Open();
            if (dialog.GetVisualDescendants().OfType<TextBox>().Single().Text != "3") throw new Exception("Reopened capacity does not match the actual host.");
            using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-host-settings-real.png"));
            await Save(dialog, original);
            File.WriteAllText(Path.Combine(output, "host-settings-results.json"), JsonSerializer.Serialize(new { passed = true,
                checks = new[] { "actual packaged host settings", "Manager save", "reopen", "original test capacity restored" } }));
        }
        finally { await client.CallAsync("host.configure", new() { ["maximumConcurrentSessions"] = original }); }
    }
}
