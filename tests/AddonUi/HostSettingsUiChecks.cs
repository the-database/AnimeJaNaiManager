using AnimeJaNaiConfEditor.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Text.Json;

internal static class HostSettingsUiChecks
{
    public static async Task RunAsync(AddonsView view, Window window, FixtureServer server, string output)
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Until(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!ready()) await Task.Delay(20, timeout.Token);
        }
        var button = view.FindControl<Button>("HostSettingsButton")!;
        Check(button.IsVisible && button.IsEnabled, "Host settings were not offered by the matching host.");
        async Task<Window> Open()
        {
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 1); return window.OwnedWindows.Single();
        }
        void Click(Window dialog, string label) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = await Open();
        var field = dialog.GetVisualDescendants().OfType<TextBox>().Single(); field.Text = "17";
        Click(dialog, "Save");
        await Until(() => dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Name == "HostSettingsError" && t.IsVisible));
        Check(server.HostSaves == 0 && server.HostCapacity == 2 && window.OwnedWindows.Count == 1, "Invalid capacity reached the host or closed the editor.");
        field.Text = "3"; Click(dialog, "Cancel"); await Until(() => window.OwnedWindows.Count == 0 && button.IsEnabled);
        Check(server.HostCapacity == 2 && server.HostSaves == 0, "Cancelled host settings were saved.");
        dialog = await Open(); dialog.GetVisualDescendants().OfType<TextBox>().Single().Text = "4";
        Click(dialog, "Save"); await Until(() => window.OwnedWindows.Count == 0 && button.IsEnabled);
        Check(server.HostCapacity == 4 && server.HostSaves == 1, "Capacity did not reach the trusted host.");
        dialog = await Open(); Check(dialog.GetVisualDescendants().OfType<TextBox>().Single().Text == "4", "Capacity was lost on reopen.");
        using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-host-settings.png"));
        Click(dialog, "Cancel"); await Until(() => window.OwnedWindows.Count == 0 && button.IsEnabled);
        server.HostEditable = false;
        dialog = await Open();
        Check(dialog.GetVisualDescendants().OfType<TextBox>().Single().IsReadOnly &&
            !dialog.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Save" && b.IsVisible), "Explicit command-line policy was editable.");
        Click(dialog, "Close"); await Until(() => window.OwnedWindows.Count == 0 && button.IsEnabled);
        server.HostEditable = true;
        File.WriteAllText(Path.Combine(output, "host-settings-results.json"), JsonSerializer.Serialize(new { passed = true,
            checks = new[] { "invalid capacity stays editable without save", "cancel preserves policy", "save and reopen", "command-line override read-only" } }));
    }
}
