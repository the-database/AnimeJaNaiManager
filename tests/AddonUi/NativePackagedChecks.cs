using AnimeJaNai.Addons.Management;
using AnimeJaNaiConfEditor.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;

internal static class NativePackagedChecks
{
    public static async Task RunAsync(AddonsView view, Window window, string root, string output)
    {
        const string id = "org.animejanai.session-controller";
        string data = Path.Combine(output, "data", "addons");
        T Find<T>(string name) where T : Control => view.FindControl<T>(name)!;
        void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task Until(Func<bool> ready)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!ready())
            {
                if (deadline.IsCancellationRequested) throw new Exception("Native UI timeout: " + Find<TextBlock>("Status").Text);
                await Task.Delay(25);
            }
        }
        var evidence = new JsonArray();
        async Task AcceptAsync(Task operation, string label)
        {
            await Until(() => window.OwnedWindows.Any());
            var dialog = window.OwnedWindows.Single();
            foreach (var permission in dialog.GetVisualDescendants().OfType<CheckBox>()) permission.IsChecked = true;
            dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await operation;
        }
        await AcceptAsync(view.ReviewPackageAsync(Path.Combine(root, "addon-development", "session-controller.ajnaddon"), window), "Install");
        Click("RefreshButton"); await Until(() => Find<Button>("RefreshButton").IsEnabled);
        if (!Find<StackPanel>("MediaSection").IsVisible) throw new Exception("Packaged native capability is unavailable.");
        await using var client = await ManagementClient.ConnectAsync(data);
        string hash = (await client.ListAsync()).Single(n => n!["id"]!.GetValue<string>() == id)!["hash"]!.GetValue<string>();
        string source = Path.Combine(root, "animejanai", "benchmarks", "480x360.mp4");
        await AcceptAsync(view.ApproveSourceAsync(window, id, hash, source), "Allow file");
        File.Copy(Path.Combine(root, "animejanai", "animejanai.conf"), Path.Combine(output, "data", "animejanai.conf"));
        await AcceptAsync(view.ApproveProfileAsync(window, id, hash), "Allow profile");
        var settings = Find<StackPanel>("SettingFields").Children.OfType<StackPanel>().ToArray();
        settings[0].Children.OfType<TextBox>().Single().Text = "2";
        Click("SaveButton"); await Until(() => Find<Button>("SaveButton").IsEnabled);
        Click("StartButton"); await Until(() => Find<Button>("StartButton").IsEnabled);
        async Task<JsonNode?> Action(string label)
        {
            Find<TextBlock>("ActionResult").Text = "";
            Find<StackPanel>("ActionFields").Children.OfType<Button>().Single(b => b.Content as string == label).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await Until(() => Find<Button>("SaveButton").IsEnabled && !string.IsNullOrWhiteSpace(Find<TextBlock>("ActionResult").Text));
            string rendered = Find<TextBlock>("ActionResult").Text!;
            var result = rendered.StartsWith('[') || rendered.StartsWith('{') ? JsonNode.Parse(rendered) : JsonValue.Create(rendered);
            if (result is JsonObject o && o["error"] is not null) throw new Exception("Packaged session action failed: " + result);
            return result;
        }
        _ = await Action("Open sessions");
        async Task<JsonArray> State(Func<JsonArray, bool> ready)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (true)
            {
                var states = (JsonArray)(await Action("Show session status"))!;
                if (states.Any(s => s!["state"]?.GetValue<string>() == "failed")) throw new Exception("Packaged processing failed: " + states);
                if (ready(states)) { evidence.Add(states.DeepClone()); return states; }
                await Task.Delay(100, deadline.Token);
            }
        }
        await State(states => states.Count == 2 && states.All(s => s!["state"]?.GetValue<string>() == "running" && s["outputWidth"]?.GetValue<long>() == 960));
        _ = await Action("Pause selected session");
        var paused = await State(states => states[0]!["paused"]?.GetValue<bool>() == true);
        double previous = paused[1]!["positionSeconds"]!.GetValue<double>();
        await State(states => states[1]!["positionSeconds"]?.GetValue<double>() > previous + .3);
        _ = await Action("Seek selected session to start");
        await State(states => (states[0]!["positionSeconds"]?.GetValue<double>() ?? -1) is >= 0 and < .1);
        _ = await Action("Resume selected session");
        await State(states => states[0]!["state"]?.GetValue<string>() == "running");
        using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-native-sessions.png"));
        Find<StackPanel>("MediaFields").GetVisualDescendants().OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Find<Button>("RefreshButton").IsEnabled && Find<TextBlock>("AddonState").Text!.Contains("Stopped"));
        var remaining = await client.CallAsync("media.selections", new() { ["id"] = id });
        if (((JsonArray)remaining!["sources"]!).Count != 0 || ((JsonArray)remaining["profiles"]!).Count != 1)
            throw new Exception("Native revocation did not preserve exactly the unrelated profile.");
        if (Directory.EnumerateDirectories(Path.Combine(data, "media-workers"), "media-*").Any()) throw new Exception("Revoking native access left session resources behind.");
        Click("RemoveButton"); await Until(() => window.OwnedWindows.Count > 0);
        window.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Remove addon").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Find<Button>("InstallButton").IsEnabled && Find<TextBlock>("AddonTitle").Text == "No addons installed");
        File.WriteAllText(Path.Combine(output, "native-results.json"), new JsonObject
        {
            ["passed"] = true, ["evidence"] = evidence,
            ["checks"] = new JsonArray("packaged native host", "actual source/profile consent", "two real GPU sessions", "2x DirectML output",
                "independent pause", "seek", "resume", "revocation stops sessions", "unrelated profile preserved", "native cleanup"),
        }.ToJsonString());
        Console.WriteLine("PASS packaged Manager -> host -> Wasm -> native GPU sessions, consent and revocation.");
    }
}
