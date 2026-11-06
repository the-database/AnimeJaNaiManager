using AnimeJaNaiConfEditor.Views;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;

internal static class LightingSettingsUiChecks
{
    public static async Task RunAsync(AddonsView view, Window window, FixtureServer server, string output, string? manifestPath)
    {
        // Run the grouped-settings regression in ordinary CI too. A real addon
        // manifest can still be supplied for its complete settings-page check.
        var manifest = JsonNode.Parse(manifestPath is not null ? File.ReadAllText(manifestPath) : """
            {"settings":{
                "device":{"type":"string","label":"Device address","description":"Choose a device.",
                    "default":"","group":"Connection"},
                "brightness":{"type":"number","label":"Brightness","description":"Raise for brighter output.",
                    "default":0.5,"minimum":0,"maximum":1,"group":"Brightness and color"},
                "gamma":{"type":"number","label":"Gamma","description":"Match the device gamma.",
                    "default":2.2,"minimum":1,"maximum":3,"group":"Advanced","advanced":true}},
             "actions":{"status":{"label":"Status","description":"Show the current device status."}}}
            """)!.AsObject();
        var definitions = manifest["settings"]!.AsObject();
        server.SettingsManifest = manifest;
        server.Values.Clear();
        foreach (var (key, definition) in definitions) server.Values[key] = definition!["default"]!.DeepClone();
        T Find<T>(string name) where T : Control => view.FindControl<T>(name)!;
        async Task Until(Func<bool> ready)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!ready()) await Task.Delay(20, timeout.Token);
        }
        void Check(bool passed, string message) { if (!passed) throw new Exception(message); }
        Find<Button>("RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Find<Button>("RefreshButton").IsEnabled);
        var groups = Find<StackPanel>("SettingFields").Children.OfType<Expander>().ToArray();
        var expectedGroups = definitions.Select(d => d.Value!["group"]!.GetValue<string>()).Distinct().ToArray();
        Check(groups.Select(g => (string)g.Header!).SequenceEqual(expectedGroups), "Lighting groups missing or out of order");
        foreach (var group in groups)
        {
            bool allAdvanced = definitions.Where(d => d.Value!["group"]!.GetValue<string>() == (string)group.Header!)
                .All(d => d.Value!["advanced"]?.GetValue<bool>() == true);
            Check(group.IsExpanded != allAdvanced, "Only all-advanced groups should start collapsed");
        }
        TextBox? brightness = null;
        foreach (var (key, definition) in definitions)
        {
            var group = groups.Single(g => (string)g.Header! == definition!["group"]!.GetValue<string>());
            var field = ((StackPanel)group.Content!).Children.OfType<StackPanel>().Single(f => f.Children.OfType<TextBlock>().First().Text == definition!["label"]!.GetValue<string>());
            Check(field.Children.OfType<TextBlock>().Any(t => t.Text == definition!["description"]!.GetValue<string>()), "Missing visible help for " + key);
            if (definition!["type"]!.GetValue<string>() == "number")
                Check(field.Children.OfType<TextBlock>().Any(t => t.Text!.StartsWith("Default:") && t.Text.Contains("Range:")), "Missing bounds for " + key);
            if (key == "brightness") brightness = field.Children.OfType<TextBox>().Single();
        }
        foreach (var (_, action) in manifest["actions"]!.AsObject())
            Check(Find<StackPanel>("ActionFields").Children.OfType<TextBlock>().Any(t => t.Text == action!["description"]!.GetValue<string>()), "Missing visible action help");
        brightness!.Text = "0.37";
        Find<Button>("SaveButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Find<Button>("SaveButton").IsEnabled);
        Check(server.Values["brightness"]!.GetValue<double>() == .37 && server.Values["gamma"]!.GetValue<double>() == 2.2, "Save lost visible or collapsed values");
        groups = Find<StackPanel>("SettingFields").Children.OfType<Expander>().ToArray();
        foreach (var group in groups)
        {
            foreach (var other in groups) other.IsExpanded = other == group;
            await Task.Delay(60);
            group.BringIntoView();
            await Task.Delay(60);
            using var image = window.CaptureRenderedFrame();
            image!.Save(Path.Combine(output, "lighting-" + ((string)group.Header!).Replace(' ', '-').ToLowerInvariant() + ".png"));
        }
        File.WriteAllText(Path.Combine(output, "lighting-settings.json"), new JsonObject { ["passed"] = true, ["settings"] = definitions.Count,
            ["actions"] = manifest["actions"]!.AsObject().Count, ["groups"] = groups.Length, ["collapsedValuesSaved"] = true }.ToJsonString());
        server.SettingsManifest = null;
        server.Values.Clear();
        foreach (var (key, value) in JsonNode.Parse("{\"enabled\":true,\"rate\":21,\"mode\":\"normal\",\"name\":\"Saved 日本語\"}")!.AsObject()) server.Values[key] = value!.DeepClone();
        Find<Button>("RefreshButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Until(() => Find<Button>("RefreshButton").IsEnabled);
    }
}
