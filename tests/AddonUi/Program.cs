using AnimeJaNai.Addons.Management;
using AnimeJaNaiConfEditor.Views;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

if (args.Length is not (1 or 2)) { Console.WriteLine("AddonUi <new-output-directory> [integrated-preview-root]"); return 2; }
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output);
string? focusedCase = Environment.GetEnvironmentVariable("AJN_UI_CASE");
Environment.SetEnvironmentVariable("ANIMEJANAI_DATA_DIR", Path.Combine(output, "data"));
Environment.SetEnvironmentVariable("ANIMEJANAI_ROOT", args.Length == 2 ? Path.GetFullPath(args[1]) : output);
if (args.Length == 1)
{
    Directory.CreateDirectory(Path.Combine(output, "addon-host/runtime"));
    File.WriteAllBytes(Path.Combine(output, "addon-host/ajn-addon.exe"), []);
    File.WriteAllBytes(Path.Combine(output, "addon-host/runtime/wasmtime.exe"), []);
}
await using var server = args.Length == 1 && focusedCase is null ? new FixtureServer(Path.Combine(output, "data", "addons")) : null;
var session = HeadlessUnitTestSession.StartNew(typeof(TestApp));
try
{
await session.Dispatch<bool>(async () =>
{
    // MainWindowViewModel captures the installation paths once per process.
    // Startup cases need their own process rather than changing those globals
    // after the ordinary UI fixture has initialized them.
    if (focusedCase == "startup-close") { await UsabilityUiChecks.ClosingStartupAsync(output); return true; }
    if (focusedCase == "missing-runtime") { await UsabilityUiChecks.MissingRuntimeAsync(output); return true; }
    if (focusedCase == "empty-installation") { await UsabilityUiChecks.EmptyInstallationAsync(output); return true; }
    if (args.Length == 2) return await RealHostChecks.RunAsync(output, Path.GetFullPath(args[1]));
    Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
    var view = new AddonsView();
    var tabs = new TabControl { Items = { new TabItem { Header = "Player", Content = new TextBlock { Text = "Player settings" } }, new TabItem { Header = "Addons", Content = view } }, SelectedIndex = 0 };
    var window = new Window { Width = 1100, Height = 820, Content = tabs, Title = "AJN addon preview" };
    view.ProtectUnsavedSettingsOnClose(window);
    window.Show();
    T Find<T>(string name) where T : Control => view.FindControl<T>(name)!;
    void Click(string name) => Find<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    async Task Until(Func<bool> ready)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!ready()) { await Task.Delay(20, deadline.Token); }
    }
    void Check(bool value, string message) { if (!value) throw new Exception(message + " Status: " + Find<TextBlock>("Status").Text); }

    await Task.Delay(150);
    Check(server!.Connections == 0, "An unused Addons tab should not connect an empty installation");
    string installed = Path.Combine(output, "data/addons/installed/org.example.fixture");
    Directory.CreateDirectory(installed); File.WriteAllText(Path.Combine(installed, "active.json"), "{}");
    tabs.SelectedIndex = 1;
    await Until(() => Find<Button>("RefreshButton").IsEnabled);
    Check(view.FindControl<Button>("ConnectButton") is null && !Find<Button>("RetryButton").IsVisible, "Opening Addons must be automatic");
    Check(Find<TextBlock>("AddonTitle").Text == "Sample addon", "Addon was not loaded");
    Check(Find<StackPanel>("SettingFields").Children.Count == 4, "Missing typed settings controls");
    tabs.SelectedIndex = 0; tabs.SelectedIndex = 1; await Task.Delay(150);
    Check(server.Connections == 1, "Reopening Addons duplicated the connection");
    await HostSettingsUiChecks.RunAsync(view, window, server!, output);
    await LoginUiChecks.RunAsync(view, window, server!, output);
    await Task.Delay(100);
    using (var frame = window.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-manager.png"));
    var fields = Find<StackPanel>("SettingFields").Children.OfType<StackPanel>().ToArray();
    fields[0].Children.OfType<CheckBox>().Single().IsChecked = false;
    fields[1].Children.OfType<TextBox>().Single().Text = "35.5";
    fields[2].Children.OfType<ComboBox>().Single().SelectedItem = "quiet";
    fields[3].Children.OfType<TextBox>().Single().Text = "Saved 日本語";
    Click("SaveButton");
    await Until(() => server!.Values["rate"]?.GetValue<double>() == 35.5 && Find<Button>("SaveButton").IsEnabled);
    Check(server!.Values["enabled"]!.GetValue<bool>() == false && server.Values["name"]!.GetValue<string>() == "Saved 日本語", "Settings were not saved faithfully");
    Click("RefreshButton"); await Until(() => Find<Button>("RefreshButton").IsEnabled);
    Check(Find<StackPanel>("SettingFields").Children.OfType<StackPanel>().Last().Children.OfType<TextBox>().Single().Text == "Saved 日本語", "Saved values were lost on refresh");
    server.InvalidSettings = ["rate"];
    Click("RefreshButton"); await Until(() => Find<Button>("RefreshButton").IsEnabled);
    Check(Find<TextBlock>("SettingsNotice").IsVisible && server.Values["rate"]!.GetValue<double>() == 35.5,
        "Repair must explain the problem without changing saved data");
    Find<StackPanel>("SettingFields").Children.OfType<StackPanel>().ElementAt(1).Children.OfType<TextBox>().Single().Text = "21";
    Click("SaveButton"); await Until(() => Find<Button>("SaveButton").IsEnabled);
    Check(server.Values["rate"]!.GetValue<double>() == 21 && !Find<TextBlock>("SettingsNotice").IsVisible, "Could not repair incompatible setting");
    var action = Find<StackPanel>("ActionFields").Children.OfType<Button>().Single();
    Check(!Find<StackPanel>("ActionFields").IsEnabled && Find<TextBlock>("ActionsNotice").IsVisible, "Stopped actions must explain how to start the addon");
    action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await Until(() => Find<TextBlock>("Status").Text == "Start this addon before using its actions.");
    Check(server.ActionCalls == 0, "Stopped action reached the temporary-action API");
    Click("StartButton"); await Until(() => server.Running && Find<Button>("StartButton").IsEnabled);
    action.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await Until(() => Find<TextBlock>("ActionResult").Text?.Contains("action received") == true);
    Click("StopButton"); await Until(() => !server.Running && Find<Button>("RefreshButton").IsEnabled);

    server.RequireStoredDataChoice = true;
    var review = view.ReviewPackageAsync("fixture.ajnaddon", window);
    await Until(() => window.OwnedWindows.Count > 0);
    var dialog = window.OwnedWindows.Single();
    var installButton = dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Install");
    Check(!installButton.IsEnabled, "Existing private data must require an explicit choice");
    dialog.GetVisualDescendants().OfType<ComboBox>().Single().SelectedIndex = 1;
    Check(installButton.IsEnabled, "Choosing to keep data should enable installation");
    var permissions = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
    Check(permissions.Length == 2 && permissions.All(p => p.IsChecked == false), "Permissions must start unchecked");
    permissions[0].IsChecked = true;
    using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-permissions.png"));
    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Install").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await review;
    Check(server.KeepStoredData == true, "The explicit private-data choice was not sent to the host");
    server.RequireStoredDataChoice = false;
    Check(server.Granted.SequenceEqual(new[] { "storage.read" }), "Approval did not match selected permissions");
    Check(server.ReviewHash == new string('a', 64), "Install approval was not bound to the reviewed hash");

    server.ReviewPermissions = new JsonArray("sessions.manage", "media.output", "network.connect");
    review = view.ReviewPackageAsync("output-fixture.ajnaddon", window);
    await Until(() => window.OwnedWindows.Count > 0);
    dialog = window.OwnedWindows.Single();
    var outputPermissions = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
    Check(outputPermissions.Length == 3 && outputPermissions.All(p => p.IsChecked == false), "Output permissions must also start unchecked");
    Check(outputPermissions.Any(p => p.Content as string == "Send processed video and audio to services you separately approve"), "Output consent needs its own clear label");
    using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-output-permissions.png"));
    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await review;
    Check(server.Granted.SequenceEqual(new[] { "storage.read" }), "Cancelling output review changed the permission grant");

    server.ReviewPermissions = new JsonArray("sessions.manage", "media.input", "network.connect");
    review = view.ReviewPackageAsync("input-fixture.ajnaddon", window);
    await Until(() => window.OwnedWindows.Count > 0);
    dialog = window.OwnedWindows.Single();
    var inputPermissions = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
    Check(inputPermissions.Length == 3 && inputPermissions.All(p => p.IsChecked == false), "Input permissions must start unchecked");
    Check(inputPermissions.Any(p => p.Content as string == "Read and process media from services you separately approve"), "Input consent needs a clear separate label");
    using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-input-permissions.png"));
    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await review;
    Check(server.Granted.SequenceEqual(new[] { "storage.read" }), "Cancelling input review changed existing grants");

    server.ReviewPermissions = new JsonArray("frames.read", "player.observe");
    review = view.ReviewPackageAsync("player-fixture.ajnaddon", window);
    await Until(() => window.OwnedWindows.Count > 0);
    dialog = window.OwnedWindows.Single();
    var playerPermissions = dialog.GetVisualDescendants().OfType<CheckBox>().ToArray();
    Check(playerPermissions.Length == 2 && playerPermissions.All(p => p.IsChecked == false), "Player observation permissions must start unchecked");
    Check(playerPermissions.Any(p => p.Content as string == "Read small image samples from videos played in AJN"), "Player observation needs a separate clear disclosure");
    using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-player-permissions.png"));
    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await review;
    Check(server.Granted.SequenceEqual(new[] { "storage.read" }), "Cancelling player observation review changed existing grants");
    File.WriteAllText(Path.Combine(output, "player-results.json"), "{\"passed\":true,\"checks\":[\"player observation default denied\",\"separate normal-player disclosure\",\"cancel preserves grants\"]}");

    Check(Find<StackPanel>("MediaSection").IsVisible, "Approved session permission did not expose media controls");
    string selectedPath = Path.Combine(output, "chosen video.mp4");
    var sourceReview = view.ApproveSourceAsync(window, "org.example.ui", new string('a', 64), selectedPath);
    await Until(() => window.OwnedWindows.Count > 0);
    dialog = window.OwnedWindows.Single();
    Check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == selectedPath), "Source consent must show the exact selected file");
    using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-media-consent.png"));
    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Cancel").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await sourceReview;
    Check(server.Sources.Count == 0, "Cancelling media consent granted file access");
    sourceReview = view.ApproveSourceAsync(window, "org.example.ui", new string('a', 64), selectedPath);
    await Until(() => window.OwnedWindows.Count > 0);
    window.OwnedWindows.Single().GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Allow file").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await sourceReview;
    Check(server.Sources.Count == 1 && server.MediaReviewHash == new string('a', 64), "Media consent was not bound to the reviewed addon");
    Directory.CreateDirectory(Path.Combine(output, "data"));
    const string savedConfiguration = "[global]\nconfig_version=3\nbackend=DirectML\n[slot_3]\nname=Saved custom\n";
    File.WriteAllText(Path.Combine(output, "data", "animejanai.conf"), savedConfiguration);
    var profileReview = view.ApproveProfileAsync(window, "org.example.ui", new string('a', 64));
    await Until(() => window.OwnedWindows.Count > 0);
    dialog = window.OwnedWindows.Single();
    dialog.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "ProfileName").Text = "Saved 日本語 profile";
    using (var frame = dialog.CaptureRenderedFrame()) frame!.Save(Path.Combine(output, "addon-profile-consent.png"));
    dialog.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Allow profile").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await profileReview;
    Check(server.ProfileConfiguration == savedConfiguration && server.Profiles.Count == 1, "Approved profile did not preserve the saved configuration");
    var mediaDraft = Find<StackPanel>("SettingFields").GetVisualDescendants().OfType<TextBox>().Last();
    string beforeMedia = mediaDraft.Text!; mediaDraft.Text = "Pending media setup";
    Find<StackPanel>("MediaFields").GetVisualDescendants().OfType<Button>().First().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    await Until(() => server.Sources.Count == 0 && Find<Button>("RefreshButton").IsEnabled);
    Check(server.Profiles.Count == 1 && !server.Running, "Revocation changed an unrelated selection or left the addon running");
    Check(mediaDraft.Text == "Pending media setup" && Find<StackPanel>("SettingFields").GetVisualDescendants().Contains(mediaDraft), "Removing media access lost unsaved settings");
    mediaDraft.Text = beforeMedia;
    await NetworkUiChecks.RunAsync(view, window, server, output);
    await LightingSettingsUiChecks.RunAsync(view, window, server, output,
        Environment.GetEnvironmentVariable("AJN_LIGHTING_MANIFEST"));
    await ListenerUiChecks.RunAsync(view, window, server, output);
    await UsabilityUiChecks.RunAsync(view, window, server, output);
    await view.CloseAsync(); window.Close();
    File.WriteAllText(Path.Combine(output, "results.json"), "{\"passed\":true,\"checks\":[\"typed settings\",\"save and reload\",\"incompatible setting repair\",\"action\",\"start/stop\",\"permissions default denied\",\"exact selected grant\",\"review hash\",\"media consent cancellation\",\"exact file and package consent\",\"saved profile snapshot\",\"media access revocation\",\"destination consent cancellation\",\"reviewed destination and package\",\"masked credential consent\",\"credential removal preserves destination\",\"destination removal preserves media\",\"output permissions default denied\",\"output permission review cancellation\",\"media output destination disclosure\",\"media input permissions default denied\",\"media input review cancellation\",\"media input destination disclosure\",\"listener controls require permission\",\"local listener consent cancellation\",\"listener package hash binding\",\"listener access removal\",\"HTTPS certificate control\",\"public listener and CORS disclosure\",\"public listener consent cancellation\"]}");
    return true;
}, CancellationToken.None);
}
catch (Exception error)
{
    Console.Error.WriteLine("FAIL " + error);
    File.WriteAllText(Path.Combine(output, "results.json"), System.Text.Json.JsonSerializer.Serialize(new { passed = false, error = error.ToString() }));
    return 1;
}
finally
{
    // Dispatch can complete its awaiter inline on the UI thread. Dispose joins
    // that thread, so it must run on a different thread even after successful checks.
    await Task.Run(session.Dispose).ConfigureAwait(false);
}
Console.WriteLine("PASS Addon Manager controls, settings, lifecycle and permission review; screenshots: " + output);
return 0;

public class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<AnimeJaNaiConfEditor.App>()
        .UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal sealed class FixtureServer : IAsyncDisposable
{
    public int ActionCalls;
    public int Connections, RemoveCalls;
    public bool FailNextStart;
    public bool RequireStoredDataChoice;
    public bool? KeepStoredData;
    public JsonObject? ExtraAddon;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task serving;
    public bool Running;
    public string[] Granted = [];
    public JsonArray ReviewPermissions = new("storage.read", "storage.write");
    public int HostCapacity = 2, HostSaves;
    public bool LoginEnabled, LoginAvailable = true;
    public int LoginSaves;
    public bool HostEditable = true;
    public string? ReviewHash;
    public string[] InvalidSettings = [];
    public JsonArray Sources = [], Profiles = [];
    public string? MediaReviewHash, ProfileConfiguration;
    public JsonArray Destinations = [];
    public bool ListenerPermission;
    public JsonArray Listeners = [];
    public JsonObject? ListenerBinding;
    public string? ListenerReviewHash;
    public string? NetworkReviewHash, NetworkReviewId, CredentialValue;
    public bool DeviceSettings;
    public JsonObject? SettingsManifest;
    public JsonObject Values = new() { ["enabled"] = true, ["rate"] = 20.0, ["mode"] = "normal", ["name"] = "Hello" };
    public FixtureServer(string directory) { serving = Task.Run(() => ServeAsync(directory)); }
    private async Task ServeAsync(string directory)
    {
        try
        {
            using var pipe = new NamedPipeServerStream(ManagementClient.PipeName(directory), PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.WaitForConnectionAsync(lifetime.Token);
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
            while (!lifetime.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(lifetime.Token); if (line is null) return;
                var request = JsonNode.Parse(line)!; string method = request["method"]!.GetValue<string>();
                var parameters = (JsonObject)request["params"]!;
                if (method == "manager.hello") Connections++;
                if (method == "addons.action") ActionCalls++;
                if (method == "addons.remove") RemoveCalls++;
                if (method == "addons.start" && FailNextStart)
                {
                    FailNextStart = false;
                    await writer.WriteLineAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(),
                        ["error"] = new JsonObject { ["code"] = -32000, ["message"] = "Permission not granted: storage.write.", ["data"] = new JsonObject { ["code"] = "permission_denied" } } }.ToJsonString());
                    continue;
                }
                JsonNode? result = method switch
                {
                    "manager.hello" => new JsonObject { ["major"] = 1, ["nativeMediaAvailable"] = true, ["networkAvailable"] = true, ["deviceSettingsAvailable"] = true, ["httpServerAvailable"] = true, ["listenerTlsAvailable"] = true, ["listenerNetworkAvailable"] = true, ["credentialsAvailable"] = true, ["hostSettingsAvailable"] = true, ["loginSettingsAvailable"] = true },
                    "host.settings" => new JsonObject { ["maximumConcurrentSessions"] = HostCapacity, ["minimum"] = 1, ["maximum"] = 16, ["editable"] = HostEditable },
                    "host.login" => new JsonObject { ["enabled"] = LoginEnabled, ["available"] = LoginAvailable, ["registeredElsewhere"] = false },
                    "addons.list" => new JsonObject { ["addons"] = new JsonArray(new JsonObject { ["id"] = "org.example.ui", ["name"] = "Sample addon", ["version"] = "0.1.0", ["running"] = Running, ["manual"] = true, ["hash"] = new string('a', 64), ["mediaPermission"] = true, ["networkPermission"] = true, ["credentialPermission"] = true, ["outputPermission"] = true, ["inputPermission"] = true }), ["nextCursor"] = null },
                    "addons.settings" => JsonNode.Parse("""{"definitions":{"enabled":{"type":"boolean","label":"Enabled"},"rate":{"type":"number","label":"Sample rate","description":"A sample numeric setting."},"mode":{"type":"choice","label":"Mode","choices":["normal","quiet"]},"name":{"type":"string","label":"Greeting","maxLength":100}},"actions":{"check":{"label":"Check status","description":"Run an addon action."}}} """),
                    "addons.logs" => new JsonArray("Sample addon connected.", "Settings and messages are isolated from player profiles."),
                    "addons.action" => new JsonObject { ["status"] = "action received" },
                    "addons.inspect" => new JsonObject { ["requiresStoredDataChoice"] = RequireStoredDataChoice, ["manifest"] = new JsonObject { ["id"] = "org.example.ui", ["name"] = "Sample addon", ["version"] = "0.1.0", ["permissions"] = ReviewPermissions.DeepClone() }, ["hash"] = new string('a', 64) },
                    "media.selections" => new JsonObject { ["sources"] = Sources.DeepClone(), ["profiles"] = Profiles.DeepClone() },
                    "network.selections" => new JsonObject { ["destinations"] = Destinations.DeepClone() },
                    "network.inspectDestination" => new JsonObject { ["origin"] = parameters["origin"]!.DeepClone(), ["protocol"] = "http", ["addresses"] = new JsonArray("127.0.0.1"), ["reviewId"] = "review-one" },
                    _ => null,
                };
                if (method == "host.configure") { HostCapacity = parameters["maximumConcurrentSessions"]!.GetValue<int>(); HostSaves++; }
                if (method == "addons.list") result!["addons"]![0]!["listenerPermission"] = ListenerPermission;
                if (method == "listeners.selections") result = new JsonObject { ["listeners"] = Listeners.DeepClone() };
                if (method == "listeners.certificates") result = new JsonObject { ["certificates"] = new JsonArray() };
                if (method == "listeners.inspect")
                {
                    ListenerBinding = new JsonObject { ["address"] = parameters["address"]!.DeepClone(), ["port"] = parameters["port"]!.DeepClone(), ["sensitiveHeaders"] = parameters["sensitiveHeaders"]!.DeepClone(), ["sensitiveQuery"] = parameters["sensitiveQuery"]!.DeepClone() };
                    foreach (string key in new[] { "scheme", "scope", "allowedHosts", "publicBaseUrl", "certificateId", "certificateHost", "cors" })
                        if (parameters.ContainsKey(key)) ListenerBinding[key] = parameters[key]?.DeepClone();
                    result = new JsonObject { ["reviewId"] = "listener-review", ["binding"] = ListenerBinding.DeepClone() };
                }
                if (method == "listeners.approve")
                {
                    ListenerReviewHash = parameters["expectedHash"]!.GetValue<string>();
                    Listeners.Add(new JsonObject { ["id"] = "listener-one", ["name"] = parameters["name"]!.DeepClone(), ["binding"] = ListenerBinding!.DeepClone() });
                }
                if (method == "listeners.revoke") { Listeners.Clear(); Running = false; }
                if (method == "addons.list" && ExtraAddon is not null) ((JsonArray)result!["addons"]!).Add(ExtraAddon.DeepClone());
                if (method == "host.configureLogin") { LoginEnabled = parameters["enabled"]!.GetValue<bool>(); LoginSaves++; }
                if (method == "addons.settings")
                {
                    if (SettingsManifest is not null)
                    {
                        result!["definitions"] = SettingsManifest["settings"]!.DeepClone();
                        result["actions"] = SettingsManifest["actions"]!.DeepClone();
                    }
                    else result!["definitions"]!["rate"]!["default"] = 20;
                    result!["values"] = Values.DeepClone();
                    if (DeviceSettings) result["definitions"]!["device"] = new JsonObject { ["type"] = "string", ["label"] = "Light address", ["default"] = "",
                        ["deviceEndpoints"] = new JsonArray(new JsonObject { ["name"] = "light-http", ["protocol"] = "http", ["port"] = 80 }, new JsonObject { ["name"] = "light-ddp", ["protocol"] = "udp", ["port"] = 4048 }) };
                    result["invalidSettings"] = new JsonArray(InvalidSettings.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
                    if (InvalidSettings.Length > 0) result["values"]!["rate"] = 20;
                }
                if (method == "addons.configure") { Values = (JsonObject)parameters["changes"]!.DeepClone(); InvalidSettings = []; result = Values.DeepClone(); }
                if (method == "addons.start") Running = true;
                if (method == "addons.stop") Running = false;
                if (method == "addons.installDev")
                {
                    KeepStoredData = parameters["keepStoredData"]?.GetValue<bool>();
                    Granted = ((JsonArray)parameters["permissions"]!).Select(v => v!.GetValue<string>()).ToArray();
                    ReviewHash = parameters["expectedHash"]!.GetValue<string>();
                }
                if (method.StartsWith("media.") && method != "media.selections") MediaReviewHash = parameters["expectedHash"]!.GetValue<string>();
                if (method.StartsWith("network.") && method != "network.selections") NetworkReviewHash = parameters["expectedHash"]!.GetValue<string>();
                if (method == "network.inspectDevice") result = new JsonObject { ["reviewId"] = "device-review", ["host"] = parameters["host"]!.DeepClone(),
                    ["destinations"] = new JsonArray(new JsonObject { ["origin"] = "http://hyperl2.local:80", ["addresses"] = new JsonArray("192.168.1.220") },
                        new JsonObject { ["origin"] = "udp://hyperl2.local:4048", ["addresses"] = new JsonArray("192.168.1.220") }) };
                if (method == "network.approveDevice")
                {
                    NetworkReviewId = parameters["reviewId"]!.GetValue<string>(); Values["device"] = "hyperl2.local"; Running = false;
                    foreach (var protocol in new[] { "http", "udp" }) Destinations.Add(new JsonObject { ["id"] = "device-" + protocol, ["name"] = "light-" + protocol,
                        ["origin"] = protocol + "://hyperl2.local:" + (protocol == "http" ? 80 : 4048), ["protocol"] = protocol, ["addresses"] = new JsonArray("192.168.1.220") });
                    result = Values.DeepClone();
                }
                if (method == "network.approveDestination")
                {
                    NetworkReviewId = parameters["reviewId"]!.GetValue<string>();
                    Destinations.Add(new JsonObject { ["id"] = "destination-one", ["name"] = parameters["name"]!.DeepClone(), ["origin"] = "http://127.0.0.1:19001", ["protocol"] = "http", ["addresses"] = new JsonArray("127.0.0.1"), ["hasCredential"] = false });
                }
                if (method == "network.setCredential")
                {
                    CredentialValue = parameters["value"]!.GetValue<string>(); Running = false;
                    Destinations[0]!["hasCredential"] = true; Destinations[0]!["credentialHeader"] = parameters["header"]!.DeepClone();
                }
                if (method == "network.removeCredential") { CredentialValue = null; Destinations[0]!["hasCredential"] = false; Destinations[0]!["credentialHeader"] = null; Running = false; }
                if (method == "network.revoke") { Destinations.Clear(); Running = false; }
                if (method == "media.approveSource") Sources.Add(new JsonObject { ["id"] = "source-one", ["name"] = "Chosen video", ["path"] = parameters["path"]!.DeepClone() });
                if (method == "media.approveProfile")
                {
                    ProfileConfiguration = parameters["configuration"]!.GetValue<string>();
                    Profiles.Add(new JsonObject { ["id"] = "profile-one", ["name"] = parameters["name"]!.DeepClone(), ["slot"] = parameters["slot"]!.DeepClone(), ["backend"] = parameters["backend"]!.DeepClone() });
                }
                if (method == "media.revoke")
                {
                    Running = false;
                    if (parameters["kind"]!.GetValue<string>() == "source") Sources.Clear(); else Profiles.Clear();
                }
                var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = request["id"]!.DeepClone(), ["result"] = result };
                await writer.WriteLineAsync(response.ToJsonString());
            }
        }
        catch (Exception error) when (error is OperationCanceledException or IOException) { }
    }
    public async ValueTask DisposeAsync() { lifetime.Cancel(); await serving; lifetime.Dispose(); }
}
