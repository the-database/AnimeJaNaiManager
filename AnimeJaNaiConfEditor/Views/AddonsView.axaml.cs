using AnimeJaNai.Addons.Management;
using AnimeJaNaiConfEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views;

public partial class AddonsView : UserControl
{
    private ManagementClient? client;
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer refreshTimer;
    private readonly Dictionary<string, Func<JsonNode?>> readSettings = new(StringComparer.Ordinal);
    private bool busy, loadingList, polling;
    private bool selectedRunning, selectedFaulted;
    private string? displayedId;
    private JsonObject savedValues = new();
    private string DataDirectory => Path.Combine(MainWindowViewModel.DataDir, "addons");
    private string HostPath => Path.Combine(MainWindowViewModel.RootDir, "addon-host", "ajn-addon.exe");
    private string RuntimePath => Path.Combine(MainWindowViewModel.RootDir, "addon-host", "runtime", "wasmtime.exe");
    internal static bool SupportInstalled => OperatingSystem.IsWindows() &&
        File.Exists(Path.Combine(MainWindowViewModel.RootDir, "addon-host", "ajn-addon.exe")) &&
        File.Exists(Path.Combine(MainWindowViewModel.RootDir, "addon-host", "runtime", "wasmtime.exe"));
    private bool HasInstalledAddons()
    {
        string installed = Path.Combine(DataDirectory, "installed");
        return Directory.Exists(installed) && Directory.EnumerateDirectories(installed)
            .Take(128).Any(d => File.Exists(Path.Combine(d, "active.json")));
    }
    private string? SelectedId => (Control<ListBox>("AddonList").SelectedItem as ListBoxItem)?.Tag as string;
    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;

    public AddonsView()
    {
        AvaloniaXamlLoader.Load(this);
        Loaded += async (_, _) =>
        {
            if (SupportInstalled && HasInstalledAddons() && !Design.IsDesignMode && client?.IsConnected != true && !HasUnsavedSettings())
                await RunAsync(ConnectAsync, announceSuccess: false);
            else if (client?.IsConnected != true)
            {
                Control<TextBlock>("Status").Text = "Choose Install addon to get started.";
                UpdateButtons();
            }
        };
        refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        refreshTimer.Tick += async (_, _) =>
        {
            if (!busy && !polling && IsEffectivelyVisible && client?.IsConnected == true)
            {
                polling = true;
                try
                {
                    if (SelectedId is not null) await RefreshSelectedStatusAsync();
                    else await client.ListAsync(lifetime.Token);
                }
                catch (Exception error) { if (!lifetime.IsCancellationRequested) ShowError(error); }
                finally { polling = false; if (!busy) UpdateButtons(); }
            }
        };
    }

    public async Task ManagerOpenedAsync()
    {
        await RunAsync(async () =>
        {
            if (SupportInstalled && HasInstalledAddons())
                await ConnectAsync();
        }, announceSuccess: false);
    }

    public async Task CloseAsync()
    {
        refreshTimer.Stop(); lifetime.Cancel();
        if (client is not null) await client.DisposeAsync();
    }

    private async Task ConnectAsync()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The addon preview currently supports Windows.");
        if (client?.IsConnected == true) { await RefreshListAsync(); return; }
        if (client is not null) { await client.DisposeAsync(); client = null; }
        Control<TextBlock>("Status").Text = "Opening addons…";
        client = await ManagementClient.ConnectOrStartAsync(DataDirectory, HostPath, RuntimePath,
            File.Exists(Path.Combine(MainWindowViewModel.RootDir, "addon-host", "native-media.json")) ? MainWindowViewModel.RootDir : null,
            cancellationToken: lifetime.Token);
        refreshTimer.Start();
        await RefreshListAsync();
        Control<TextBlock>("Status").Text = "Addons are ready. Choose Install addon to add a package.";
    }

    private async Task<JsonNode?> CallAsync(string method, JsonObject? parameters = null) =>
        await (client ?? throw new IOException("Addon support is unavailable. Choose Try again.")).CallAsync(method, parameters, lifetime.Token);

    private async Task RefreshListAsync(string? preferredId = null)
    {
        string? selected = preferredId ?? SelectedId;
        var data = await client!.ListAsync(lifetime.Token);
        var list = Control<ListBox>("AddonList");
        var items = data.Select(node => new ListBoxItem
        {
            Tag = node!["id"]!.GetValue<string>(),
            Content = new TextBlock { Text = RowText(node), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4) },
        }).ToArray();
        loadingList = true;
        try { list.ItemsSource = items; list.SelectedItem = items.FirstOrDefault(i => (string)i.Tag! == selected) ?? items.FirstOrDefault(); }
        finally { loadingList = false; }
        await LoadSelectedAsync();
    }

    private static string RowText(JsonNode node) => node["name"]!.GetValue<string>() + "\n" +
        (node["running"]?.GetValue<bool>() == true ? "Running" : "Stopped") + " · " + (node["version"]?.GetValue<string>() ?? "Unavailable");

    private async Task LoadSelectedAsync()
    {
        displayedId = SelectedId; selectedRunning = false; selectedFaulted = false; savedValues = new();
        readSettings.Clear(); Control<StackPanel>("SettingFields").Children.Clear(); Control<StackPanel>("ActionFields").Children.Clear();
        Control<TextBlock>("ActionResult").Text = ""; Control<TextBox>("LogText").Text = "";
        Control<Button>("SaveButton").IsVisible = false;
        Control<TextBlock>("SettingsNotice").IsVisible = false;
        Control<StackPanel>("MediaSection").IsVisible = false;
        Control<StackPanel>("NetworkSection").IsVisible = false;
        Control<StackPanel>("ListenerSection").IsVisible = false;
        Control<TextBlock>("RuntimeNotice").IsVisible = false;
        Control<TextBlock>("ActionsNotice").IsVisible = false;
        string? id = SelectedId;
        if (id is null) { Control<TextBlock>("AddonTitle").Text = "No addons installed"; Control<TextBlock>("AddonState").Text = "Choose Install addon and select an .ajnaddon file. You will review its permissions before anything is installed."; return; }
        Control<TextBlock>("AddonTitle").Text = id;
        await RefreshSelectedStatusAsync();
        var data = (JsonObject)(await CallAsync("addons.settings", new() { ["id"] = id }))!;
        var values = (JsonObject)data["values"]!;
        savedValues = (JsonObject)values.DeepClone();
        if (data["invalidSettings"] is JsonArray { Count: > 0 } invalid)
        {
            Control<TextBlock>("SettingsNotice").Text = "This addon version cannot use some saved settings: " +
                string.Join(", ", invalid.Select(v => v!.GetValue<string>())) + ". Defaults are shown for those fields. Review and save to apply them; saved data has not been changed.";
            Control<TextBlock>("SettingsNotice").IsVisible = true;
        }
        var groups = new Dictionary<string, Expander>(StringComparer.Ordinal);
        foreach (var (key, schema) in data["definitions"] as JsonObject ?? new JsonObject())
        {
            var definition = (JsonObject)schema!;
            var destination = Control<StackPanel>("SettingFields");
            if (definition["group"] is JsonValue groupValue)
            {
                string groupName = groupValue.GetValue<string>();
                bool advanced = definition["advanced"]?.GetValue<bool>() == true;
                if (!groups.TryGetValue(groupName, out var group))
                {
                    group = new Expander { Header = groupName, IsExpanded = !advanced,
                        HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        Content = new StackPanel { Spacing = 20, Margin = new Thickness(0, 8, 0, 12) } };
                    groups.Add(groupName, group);
                    destination.Children.Add(group);
                }
                else if (!advanced) group.IsExpanded = true;
                destination = (StackPanel)group.Content!;
            }
            var field = new StackPanel { Spacing = 5 };
            field.Children.Add(new TextBlock { Text = definition["label"]!.GetValue<string>(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            switch (definition["type"]!.GetValue<string>())
            {
                case "boolean":
                    var checkbox = new CheckBox { IsChecked = values[key]!.GetValue<bool>(), Content = "Enabled" };
                    field.Children.Add(checkbox); readSettings[key] = () => JsonValue.Create(checkbox.IsChecked == true); break;
                case "choice":
                    var choices = ((JsonArray)definition["choices"]!).Select(c => c!.GetValue<string>()).ToArray();
                    var select = new ComboBox { ItemsSource = choices, SelectedItem = values[key]!.GetValue<string>(), HorizontalAlignment = HorizontalAlignment.Stretch };
                    field.Children.Add(select); readSettings[key] = () => JsonValue.Create(select.SelectedItem as string ?? ""); break;
                case "number":
                    var number = new TextBox { Text = values[key]!.ToJsonString(), MinWidth = 140, MaxWidth = 220, HorizontalAlignment = HorizontalAlignment.Left };
                    field.Children.Add(number);
                    field.Children.Add(new TextBlock { Text = "Default: " + definition["default"]!.ToJsonString() +
                        (definition["minimum"] is not null && definition["maximum"] is not null
                            ? "  ·  Range: " + definition["minimum"]!.ToJsonString() + " to " + definition["maximum"]!.ToJsonString() : ""),
                        FontSize = 12, Opacity = .7, TextWrapping = TextWrapping.Wrap });
                    readSettings[key] = () => (double.TryParse(number.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out double n) ||
                        double.TryParse(number.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out n)) && double.IsFinite(n)
                        ? JsonValue.Create(n) : throw new FormatException("Enter a valid number for " + definition["label"]!.GetValue<string>() + "."); break;
                default:
                    var text = new TextBox { Text = values[key]!.GetValue<string>(), MaxLength = definition["maxLength"]?.GetValue<int>() ?? 4096 };
                    field.Children.Add(text); readSettings[key] = () => JsonValue.Create(text.Text ?? "");
                    if (definition["deviceEndpoints"] is JsonArray)
                    {
                        text.PlaceholderText = "Device hostname or IP address";
                        var connect = new Button { Content = "Connect", HorizontalAlignment = HorizontalAlignment.Left,
                            IsEnabled = client?.ServerInfo["deviceSettingsAvailable"]?.GetValue<bool>() == true };
                        connect.Click += async (_, _) => await RunAsync(async () =>
                        {
                            if (selectedHash is not string hash) return;
                            var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open device settings.");
                            string? host = await ConnectDeviceAsync(owner, id, hash, key, definition["label"]!.GetValue<string>(), text.Text ?? "");
                            if (host is not null) { text.Text = host; savedValues[key] = host; }
                        });
                        field.Children.Add(connect);
                    }
                    break;
            }
            if (definition["description"] is JsonValue description)
                field.Children.Add(new TextBlock { Text = description.GetValue<string>(), TextWrapping = TextWrapping.Wrap, Opacity = .8 });
            destination.Children.Add(field);
        }
        Control<Button>("SaveButton").IsVisible = readSettings.Count > 0;
        foreach (var (key, schema) in data["actions"] as JsonObject ?? new JsonObject())
        {
            var action = new Button { Content = schema!["label"]!.GetValue<string>(), HorizontalAlignment = HorizontalAlignment.Left };
            if (schema["description"] is JsonValue description) ToolTip.SetTip(action, description.GetValue<string>());
            action.Click += async (_, _) => await RunAsync(async () =>
            {
                await RefreshSelectedStatusAsync();
                if (!selectedRunning) { Control<TextBlock>("Status").Text = "Start this addon before using its actions."; return; }
                var result = await CallAsync("addons.action", new() { ["id"] = id, ["action"] = key });
                Control<TextBlock>("ActionResult").Text = ActionText(result);
                await RefreshSelectedStatusAsync();
            }, announceSuccess: false);
            Control<StackPanel>("ActionFields").Children.Add(action);
            if (schema["description"] is JsonValue actionDescription)
                Control<StackPanel>("ActionFields").Children.Add(new TextBlock { Text = actionDescription.GetValue<string>(),
                    TextWrapping = TextWrapping.Wrap, Opacity = .8, Margin = new Thickness(0, 0, 0, 10) });
        }
        await RefreshMediaAsync();
        await RefreshNetworkAsync();
        await RefreshListenersAsync();
    }

    private async Task RefreshSelectedStatusAsync()
    {
        string? id = SelectedId; if (id is null) return;
        var list = await client!.ListAsync(lifetime.Token);
        if (SelectedId != id) return;
        var row = list.FirstOrDefault(n => n!["id"]!.GetValue<string>() == id);
        if (row is null) return;
        selectedHash = row["hash"]?.GetValue<string>();
        mediaPermission = row["mediaPermission"]?.GetValue<bool>() == true;
        networkPermission = row["networkPermission"]?.GetValue<bool>() == true;
        listenerPermission = row["listenerPermission"]?.GetValue<bool>() == true;
        credentialPermission = row["credentialPermission"]?.GetValue<bool>() == true;
        outputPermission = row["outputPermission"]?.GetValue<bool>() == true;
        inputPermission = row["inputPermission"]?.GetValue<bool>() == true;
        selectedRunning = row["running"]?.GetValue<bool>() == true;
        selectedFaulted = row["error"] is JsonValue;
        Control<TextBlock>("AddonTitle").Text = row["name"]!.GetValue<string>();
        Control<TextBlock>("AddonState").Text = id + " · " + (row["version"]?.GetValue<string>() ?? "Unavailable") + "\n" +
            (row["running"]!.GetValue<bool>() ? "Running" : "Stopped") + (row["error"] is JsonValue error ? "\n" + error.GetValue<string>() : "");
        Control<Button>("StartButton").Tag = row["manual"]?.GetValue<bool>() == true;
        Control<TextBlock>("RuntimeNotice").IsVisible = true;
        Control<TextBlock>("RuntimeNotice").Text = selectedRunning
            ? "Addons started with Start addon keep running after Manager closes. Stop addon ends their work."
            : "Stopped. Some addons also start automatically when Manager, a player, or Windows starts, according to their setup.";
        if (Control<ListBox>("AddonList").SelectedItem is ListBoxItem item && item.Content is TextBlock label) label.Text = RowText(row);
        var logs = (JsonArray)(await CallAsync("addons.logs", new() { ["id"] = id }))!;
        if (SelectedId == id) Control<TextBox>("LogText").Text = string.Join(Environment.NewLine, logs.Select(l => l!.GetValue<string>()));
    }

    private async Task RunAsync(Func<Task> action, bool announceSuccess = true)
    {
        if (busy || lifetime.IsCancellationRequested) return;
        Control<Expander>("ErrorDetails").IsVisible = false;
        busy = true; UpdateButtons();
        try { await action(); if (announceSuccess) Control<TextBlock>("Status").Text = "Done."; }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { ShowError(error); }
        finally { busy = false; UpdateButtons(); }
    }

    private void UpdateButtons()
    {
        bool connected = client?.IsConnected == true;
        Control<Button>("RetryButton").IsVisible = !connected && !busy && HasInstalledAddons();
        Control<Button>("RetryButton").IsEnabled = !busy;
        Control<Button>("InstallButton").IsEnabled = SupportInstalled && !busy;
        Control<Button>("RefreshButton").IsEnabled = connected && !busy;
        Control<Button>("HostSettingsButton").IsVisible = connected && client?.ServerInfo["hostSettingsAvailable"]?.GetValue<bool>() == true;
        Control<Button>("HostSettingsButton").IsEnabled = connected && !busy;
        Control<Button>("LoginSettingsButton").IsVisible = connected && client?.ServerInfo["loginSettingsAvailable"]?.GetValue<bool>() == true;
        Control<Button>("LoginSettingsButton").IsEnabled = connected && !busy;
        foreach (string name in new[] { "StartButton", "StopButton", "RollbackButton", "RemoveButton", "SaveButton" })
            Control<Button>(name).IsEnabled = connected && !busy && SelectedId is not null && (name != "StartButton" || Control<Button>(name).Tag is true);
        Control<Button>("StopButton").IsEnabled = connected && !busy && SelectedId is not null && (selectedRunning || selectedFaulted);
        Control<ListBox>("AddonList").IsEnabled = !busy;
        Control<StackPanel>("SettingFields").IsEnabled = !busy;
        Control<StackPanel>("ActionFields").IsEnabled = connected && !busy && selectedRunning;
        Control<TextBlock>("ActionsNotice").IsVisible = SelectedId is not null && Control<StackPanel>("ActionFields").Children.Count > 0 && !selectedRunning;
        Control<TextBlock>("ActionsNotice").Text = Control<Button>("StartButton").Tag is true
            ? "Start this addon to use its actions. It will keep running until you stop it."
            : "This addon must be running to use its actions. It starts from its declared player, Manager or Windows startup event.";
        Control<StackPanel>("MediaSection").IsEnabled = connected && !busy;
        Control<StackPanel>("NetworkSection").IsEnabled = connected && !busy;
        Control<StackPanel>("ListenerSection").IsEnabled = connected && !busy;
    }

    private async void RetryClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (HasUnsavedSettings() && !await ConfirmAsync("Discard unsaved settings?", "Reopening addons restores their last saved settings.", "Discard changes")) return;
        await ConnectAsync();
    }, announceSuccess: false);
    private async void RefreshClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (HasUnsavedSettings() && !await ConfirmAsync("Discard unsaved settings?", "Reloading restores the last saved values for this addon.", "Discard changes")) return;
        await ConnectAsync();
    }, announceSuccess: false);
    private async void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingList) return;
        if (HasUnsavedSettings())
        {
            var list = Control<ListBox>("AddonList"); loadingList = true;
            try { list.SelectedItem = list.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == displayedId); }
            finally { loadingList = false; }
            Control<TextBlock>("Status").Text = "Save your settings before switching addons, or choose Reload to discard your edits.";
            return;
        }
        await RunAsync(LoadSelectedAsync, announceSuccess: false);
    }
    private async void StartClick(object? sender, RoutedEventArgs e) => await SelectedOperationAsync("addons.start");
    private async void StopClick(object? sender, RoutedEventArgs e) => await SelectedOperationAsync("addons.stop");
    private async void RollbackClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SelectedId is not string id) return;
        if (!await ConfirmAsync("Restore the previous version?", "This stops the addon and restores its previous package and permission choices. Saved settings and data are kept; unsaved edits are discarded.", "Restore version")) return;
        await CallAsync("addons.rollback", new() { ["id"] = id }); await RefreshListAsync();
        Control<TextBlock>("Status").Text = "Previous addon version restored. Review its settings and access before starting it.";
    }, announceSuccess: false);
    private async void RemoveClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SelectedId is not string id) return;
        if (!await ConfirmAsync("Remove this addon?", "This stops the addon and removes its access to media, services and devices. Saved settings and data are kept for reinstallation. Unsaved edits are discarded.", "Remove addon")) return;
        await CallAsync("addons.remove", new() { ["id"] = id }); await RefreshListAsync();
        Control<TextBlock>("Status").Text = "Addon removed. Its settings and saved data are preserved.";
    }, announceSuccess: false);
    private async Task SelectedOperationAsync(string method) => await RunAsync(async () =>
    {
        if (SelectedId is not string id) return;
        await CallAsync(method, new() { ["id"] = id }); await RefreshSelectedStatusAsync();
    });
    private async void SaveClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SelectedId is not string id) return;
        var changes = new JsonObject(); foreach (var (key, read) in readSettings) changes[key] = read();
        var updated = (JsonObject)(await CallAsync("addons.configure", new() { ["id"] = id, ["changes"] = changes }))!;
        savedValues = (JsonObject)updated.DeepClone();
        Control<TextBlock>("SettingsNotice").IsVisible = false;
    });

    private async void InstallClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open the package picker.");
        var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an addon package", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("AJN addon") { Patterns = new[] { "*.ajnaddon" } } },
        });
        string? path = picked.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
        await ReviewPackageAsync(path, owner);
    }, announceSuccess: false);

    internal async Task ReviewPackageAsync(string path, Window owner)
    {
        if (HasUnsavedSettings() && !await ConfirmAsync("Discard unsaved settings?", "Installing an addon reloads the list and restores saved settings. Choose Cancel to save your edits first.", "Discard changes")) return;
        if (client?.IsConnected != true) await ConnectAsync();
        var inspected = (JsonObject)(await CallAsync("addons.inspect", new() { ["path"] = path }))!;
        var manifest = (JsonObject)inspected["manifest"]!;
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = manifest["name"]!.GetValue<string>(), FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = manifest["id"]!.GetValue<string>() + " · " + manifest["version"]!.GetValue<string>(), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Local development package. Publisher identity has not been verified.", TextWrapping = TextWrapping.Wrap });
        ComboBox? storedData = null;
        if (inspected["requiresStoredDataChoice"]?.GetValue<bool>() == true)
        {
            content.Children.Add(new TextBlock { Text = "A different package used this addon ID before. Saved settings and data may contain secrets. Only keep them if you trust this package with that information; resetting permanently deletes the old saved data.", TextWrapping = TextWrapping.Wrap });
            storedData = new ComboBox { ItemsSource = new[] { "Choose what to do with saved data…", "Keep saved data — I trust this package", "Reset saved settings and data" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            content.Children.Add(storedData);
        }
        content.Children.Add(new TextBlock { Text = "Choose the permissions to grant:", FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = "Only selected permissions are allowed. Features that need an unchecked permission may not work. You can review your choices later by installing this package again.", TextWrapping = TextWrapping.Wrap });
        var grants = new List<(string Permission, CheckBox Check)>();
        foreach (var value in (JsonArray)manifest["permissions"]!)
        {
            string permission = value!.GetValue<string>();
            string label = permission switch
            {
                "log.write" => "Write addon log messages",
                "storage.read" => "Read this addon's saved data",
                "storage.write" => "Save this addon's own data",
                "sessions.manage" => "Process approved media using profiles you separately approve",
                "frames.read" => "Read small image samples within the media access you grant",
                "player.observe" => "Read small image samples from videos played in AJN",
                "player.analysis" => "Request optional GPU character masks (downloads a verified model on first use)",
                "player.sceneDetection" => "Analyze video frame pairs and change RIFE scene-cut decisions",
                "network.connect" => "Exchange data with services and devices you approve",
                "network.listen" => "Accept client connections on addresses and ports you approve",
                "network.proxy" => "Forward HTTP and WebSocket traffic to services you approve",
                "credentials.delegate" => "Use each client's credentials with the upstream service you approve",
                "credentials.use" => "Use saved credentials for services you approve",
                "media.output" => "Send processed video and audio to services you separately approve",
                "media.input" => "Read and process media from services you separately approve",
                _ => permission,
            };
            var check = new CheckBox { Content = label, IsChecked = false };
            grants.Add((permission, check)); content.Children.Add(check);
        }
        if (grants.Count == 0) content.Children.Add(new TextBlock { Text = "This addon requests no additional permissions." });
        var dialog = new Window { Title = "Install addon", Width = 580, MaxHeight = 640, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = content,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; cancel.Click += (_, _) => dialog.Close(false);
        var install = new Button { Content = "Install", IsEnabled = storedData is null };
        if (storedData is not null) storedData.SelectionChanged += (_, _) => install.IsEnabled = storedData.SelectedIndex > 0;
        install.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(install); content.Children.Add(buttons);
        if (!await dialog.ShowDialog<bool>(owner)) return;
        var approved = new JsonArray(grants.Where(g => g.Check.IsChecked == true).Select(g => (JsonNode?)JsonValue.Create(g.Permission)).ToArray());
        var request = new JsonObject { ["path"] = path, ["expectedHash"] = inspected["hash"]!.DeepClone(), ["permissions"] = approved };
        if (storedData is not null) request["keepStoredData"] = storedData.SelectedIndex == 1;
        await CallAsync("addons.installDev", request);
        await RefreshListAsync(manifest["id"]!.GetValue<string>());
        Control<TextBlock>("Status").Text = "Addon installed. " + (selectedRunning ? "It is running." : Control<Button>("StartButton").Tag is true ? "Choose Start addon when you are ready to use it." : "It will start from its configured startup event.");
    }
}
