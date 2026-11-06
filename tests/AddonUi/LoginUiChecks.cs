using AnimeJaNaiConfEditor.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Text.Json;

internal static class LoginUiChecks
{
    public static async Task RunAsync(AddonsView view, Window owner, FixtureServer server, string output)
    {
        async Task Until(Func<bool> ready)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            while (!ready()) await Task.Delay(20, deadline.Token);
        }
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var button = view.FindControl<Button>("LoginSettingsButton")!;
        async Task<Window> Open()
        {
            Check(button.IsVisible && button.IsEnabled, "Login startup was not offered by the matching host.");
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => owner.OwnedWindows.Count == 1); return owner.OwnedWindows.Single();
        }
        void Click(Window dialog, string label) => dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var dialog = await Open(); var checkbox = dialog.GetVisualDescendants().OfType<CheckBox>().Single();
        Check(checkbox.IsChecked == false, "Login startup must start off."); checkbox.IsChecked = true;
        Click(dialog, "Cancel"); await Until(() => owner.OwnedWindows.Count == 0 && button.IsEnabled);
        Check(server.LoginSaves == 0 && !server.LoginEnabled, "Cancel registered startup.");
        dialog = await Open(); dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
        Click(dialog, "Save"); await Until(() => owner.OwnedWindows.Count == 0 && button.IsEnabled);
        Check(server.LoginSaves == 1 && server.LoginEnabled, "Explicit login opt-in was not saved.");
        dialog = await Open(); checkbox = dialog.GetVisualDescendants().OfType<CheckBox>().Single();
        Check(checkbox.IsChecked == true, "Login setting was lost on reopen.");
        using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-login-settings.png"));
        checkbox.IsChecked = false; Click(dialog, "Save"); await Until(() => owner.OwnedWindows.Count == 0 && button.IsEnabled);
        Check(!server.LoginEnabled && server.LoginSaves == 2, "Login opt-out was not saved.");
        server.LoginAvailable = false; dialog = await Open();
        Check(!dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsEnabled, "A missing launcher was offered for activation.");
        Click(dialog, "Cancel"); await Until(() => owner.OwnedWindows.Count == 0 && button.IsEnabled); server.LoginAvailable = true;
        File.WriteAllText(Path.Combine(output, "login-results.json"), JsonSerializer.Serialize(new { passed = true,
            checks = new[] { "off by default", "cancel preserves choice", "save and reopen opt-in", "disable startup", "missing launcher cannot enable" } }));
    }
}
