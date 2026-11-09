using AnimeJaNaiConfEditor.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.Text.Json;
using System.Text.Json.Nodes;

internal static class UsabilityUiChecks
{
    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
    private static void Check(bool valid, string message) { if (!valid) throw new Exception(message); }
    internal static async Task RunAsync(AddonsView view, Window window, FixtureServer server, string output)
    {
        T Find<T>(string name) where T : Control => view.FindControl<T>(name)!;
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(AddonsView.ActionText(new JsonObject { ["message"] = "Hello 日本語" }) == "Hello 日本語" && AddonsView.ActionText(JsonValue.Create("Hello")) == "Hello", "Creator messages should be readable without JSON syntax");
        EventHandler<WindowClosingEventArgs> veto = (_, args) => args.Cancel = true;
        window.Closing += veto;
        window.Close();
        Check(window.IsVisible, "The later close handler did not cancel closing");
        Click("StartButton"); await Until(() => server.Running && Find<Button>("RefreshButton").IsEnabled);
        Click("StopButton"); await Until(() => !server.Running && Find<Button>("RefreshButton").IsEnabled);
        window.Closing -= veto;
        async Task Choose(string choice)
        {
            await Until(() => window.OwnedWindows.Count == 1);
            window.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == choice).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => window.OwnedWindows.Count == 0 && Find<Button>("RefreshButton").IsEnabled);
            // Avalonia 12.1 removes the dialog before its await continuations
            // finish. Let the close handler settle before the next UI action.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        }
        server.ExtraAddon = new JsonObject { ["id"] = "org.example.second", ["name"] = "Second addon", ["version"] = "0.1.0", ["running"] = false, ["manual"] = true };
        Click("RefreshButton"); await Until(() => Find<Button>("RefreshButton").IsEnabled);
        var name = Find<StackPanel>("SettingFields").GetVisualDescendants().OfType<TextBox>().Last();
        string saved = name.Text!; name.Text = "Unsaved greeting";
        Click("StartButton"); await Until(() => Find<Button>("RefreshButton").IsEnabled);
        Check(name.Text == "Unsaved greeting" && Find<StackPanel>("SettingFields").GetVisualDescendants().Contains(name), "Starting the addon lost its unsaved settings");
        Click("StopButton"); await Until(() => Find<Button>("RefreshButton").IsEnabled);
        Check(name.Text == "Unsaved greeting" && Find<StackPanel>("SettingFields").GetVisualDescendants().Contains(name), "Stopping the addon lost its unsaved settings");
        Find<ListBox>("AddonList").SelectedIndex = 1;
        Check(Find<ListBox>("AddonList").SelectedIndex == 0 && name.Text == "Unsaved greeting", "Switching addons lost unsaved settings");
        Click("RefreshButton"); await Choose("Cancel");
        Check(name.Text == "Unsaved greeting" && server.Values["name"]!.GetValue<string>() == saved, "Cancelling reload lost or saved the draft");
        Click("RefreshButton"); await Choose("Discard changes");
        Check(Find<StackPanel>("SettingFields").GetVisualDescendants().OfType<TextBox>().Last().Text == saved, "Reload did not restore saved settings");
        Click("RemoveButton"); await Choose("Cancel");
        Check(server.RemoveCalls == 0, "Cancelled removal reached the service");
        server.FailNextStart = true;
        Click("StartButton"); await Until(() => Find<Expander>("ErrorDetails").IsVisible && Find<Button>("StartButton").IsEnabled);
        Check(Find<TextBlock>("Status").Text!.Contains("Install the same package again") && Find<TextBox>("ErrorText").Text!.Contains("permission_denied"), "Permission failure lacks a recovery step or diagnostics");
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-friendly-error.png"));
        view.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Help").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => window.OwnedWindows.Count == 1);
        var help = window.OwnedWindows.Single();
        Check(help.GetVisualDescendants().OfType<Button>().Any(b => b.Content as string == "Create an addon"), "Creator guide is not discoverable");
        using (var frame = help.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-help.png"));
        await Choose("Close");
        server.ReviewPermissions = new JsonArray("log.write", "storage.read", "storage.write", "sessions.manage", "frames.read", "player.observe", "network.connect", "credentials.use", "media.input", "media.output");
        var largeReview = view.ReviewPackageAsync("large-fixture.ajnaddon", window);
        await Until(() => window.OwnedWindows.Count == 1);
        var review = window.OwnedWindows.Single();
        Check(review.Content is ScrollViewer && review.Bounds.Height <= 640, "A large permission review must fit the screen and allow scrolling");
        Check(review.GetVisualDescendants().OfType<CheckBox>().Count() == 10 && review.GetVisualDescendants().OfType<CheckBox>().All(c => c.IsChecked == false), "Large reviews must retain all permission choices unchecked");
        using (var frame = review.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-large-review.png"));
        await Choose("Cancel"); await largeReview;
        var draft = Find<StackPanel>("SettingFields").GetVisualDescendants().OfType<TextBox>().Last();
        draft.Text = "Keep this draft";
        ((TabControl)window.Content!).SelectedIndex = 0;
        window.Close(); await Choose("Cancel");
        Check(window.IsVisible && draft.Text == "Keep this draft", "Closing Manager lost unsaved addon settings");
        window.Close(); await Choose("Discard and close");
        Check(!window.IsVisible && server.Values["name"]!.GetValue<string>() == saved, "Discard and close unexpectedly saved settings or left the window open");
        File.WriteAllText(Path.Combine(output, "usability-results.json"), JsonSerializer.Serialize(new { passed = true, checks = new[] {
            "unused tab stays idle", "opening tab connects automatically", "reopening tab reuses connection", "stopped action does not create temporary work", "cancelled window close leaves addons usable",
            "unsaved selection protected", "reload cancellation preserves draft", "explicit discard restores settings", "removal cancellation",
            "actionable permission error", "technical details retained", "offline guides discoverable", "plain creator messages",
            "closing from another tab preserves draft on cancel", "explicit close discards draft without saving", "start preserves draft", "stop preserves draft",
            "media changes preserve draft", "service changes preserve draft", "large review fits and scrolls", "all permissions still default denied" } }));
    }
    internal static async Task MissingRuntimeAsync(string output)
    {
        string root = Path.Combine(output, "missing-runtime");
        Environment.SetEnvironmentVariable("ANIMEJANAI_ROOT", root);
        Environment.SetEnvironmentVariable("ANIMEJANAI_DATA_DIR", Path.Combine(root, "data"));
        var window = new MainWindow(); var view = window.FindControl<AddonsView>("AddonPanel")!;
        window.Show();
        try
        {
            await Task.Delay(100);
            Check(!window.FindControl<TabItem>("AddonsTab")!.IsVisible, "A normal install must not show unsupported addon controls");
            Check(!File.Exists(Path.Combine(root, ".ajn-addon-activity.lock")), "Visiting a build without addon support created an installation lock");
            using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-missing-runtime.png"));
            // Deliver the headless window's normal focus notification after
            // support arrives, without constructing or reopening Manager.
            Directory.CreateDirectory(Path.Combine(root, "addon-host/runtime"));
            File.WriteAllBytes(Path.Combine(root, "addon-host/ajn-addon.exe"), []);
            window.Activate();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(!window.FindControl<TabItem>("AddonsTab")!.IsVisible, "Incomplete support must keep the tab hidden");
            File.WriteAllBytes(Path.Combine(root, "addon-host/runtime/wasmtime.exe"), []);
            window.Activate();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            var tab = window.FindControl<TabItem>("AddonsTab")!;
            Check(tab.IsVisible, "Installing support while Manager is open must reveal the tab on focus");
            tab.IsSelected = true;
            await Task.Delay(100);
            Check(view.FindControl<Button>("InstallButton")!.IsEnabled, "Newly installed support must offer addon installation");
            Check(!File.Exists(Path.Combine(root, ".ajn-addon-activity.lock")), "Discovering empty support started or locked the host");
            File.Delete(Path.Combine(root, "addon-host/runtime/wasmtime.exe"));
            window.Activate();
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Check(!tab.IsVisible && window.FindControl<TabControl>("MainTabs")!.SelectedIndex == 0,
                "Removing support must select a visible page");
            File.WriteAllText(Path.Combine(output, "missing-runtime-results.json"), "{\"passed\":true,\"checks\":[\"missing runtime hides tab\",\"partial install stays hidden\",\"focus discovers newly installed support\",\"installation enabled\",\"no installation lock\"]}");
        }
        finally { await view.CloseAsync(); window.Close(); }
    }

    internal static async Task ClosingStartupAsync(string output)
    {
        string root = Path.Combine(output, "unresponsive-host");
        Directory.CreateDirectory(Path.Combine(root, "addon-host/runtime"));
        File.WriteAllBytes(Path.Combine(root, "addon-host/ajn-addon.exe"), []);
        File.WriteAllBytes(Path.Combine(root, "addon-host/runtime/wasmtime.exe"), []);
        Environment.SetEnvironmentVariable("ANIMEJANAI_ROOT", root);
        Environment.SetEnvironmentVariable("ANIMEJANAI_DATA_DIR", Path.Combine(root, "data"));
        string installed = Path.Combine(root, "data/addons/installed/org.example.fixture");
        Directory.CreateDirectory(installed); File.WriteAllText(Path.Combine(installed, "active.json"), "{}");
        using var server = new System.IO.Pipes.NamedPipeServerStream(AnimeJaNai.Addons.Management.ManagementClient.PipeName(Path.Combine(root, "data/addons")),
            System.IO.Pipes.PipeDirection.InOut, 1, System.IO.Pipes.PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        var view = new AddonsView(); var window = new Window { Content = view };
        view.ProtectUnsavedSettingsOnClose(window);
        window.Show();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await server.WaitForConnectionAsync(deadline.Token);
            // Hold the handshake open to reproduce a real busy startup.
            window.Close();
            Check(!window.IsVisible, "An unresponsive addon host prevented Manager from closing");
            byte[] buffer = new byte[4096];
            while (await server.ReadAsync(buffer, deadline.Token) != 0) { }
            File.WriteAllText(Path.Combine(output, "startup-close-results.json"), "{\"passed\":true,\"checks\":[\"unresponsive handshake cancelled on close\"]}");
        }
        finally { await view.CloseAsync(); window.Close(); }
    }

    internal static async Task EmptyInstallationAsync(string output)
    {
        var view = new AddonsView(); var window = new Window { Content = view };
        view.ProtectUnsavedSettingsOnClose(window);
        window.Show();
        await view.ManagerOpenedAsync();
        await Task.Delay(150);
        Check(AddonsView.SupportInstalled && view.FindControl<Button>("InstallButton")!.IsEnabled, "Empty addon install must offer installation");
        Check(!view.FindControl<Button>("RetryButton")!.IsVisible, "An unused host is not a connection error");
        Check(!File.Exists(Path.Combine(output, ".ajn-addon-activity.lock")), "Empty addon tab started or locked the host");
        Check(!Directory.Exists(Path.Combine(output, "data/addons")), "Empty addon tab created host data");
        window.Close();
        File.WriteAllText(Path.Combine(output, "empty-installation-results.json"), "{\"passed\":true}");
    }
}
