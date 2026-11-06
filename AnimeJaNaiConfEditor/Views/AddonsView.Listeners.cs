using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views;

public partial class AddonsView
{
    private bool listenerPermission;
    private async Task RefreshListenersAsync()
    {
        var section = Control<StackPanel>("ListenerSection");
        var fields = Control<StackPanel>("ListenerFields"); fields.Children.Clear();
        section.IsVisible = listenerPermission;
        if (!listenerPermission || SelectedId is not string id || selectedHash is not string hash) return;
        bool available = client?.ServerInfo["httpServerAvailable"]?.GetValue<bool>() == true;
        Control<Button>("ApproveListenerButton").IsVisible = available;
        Control<TextBlock>("ListenerNotice").Text = available
            ? "Approve where clients can connect. Local access is the default. Removing access or replacing a certificate stops the addon and its listeners."
            : "This AJN build does not include addon listeners.";
        if (!available) return;
        var list = (JsonObject)(await CallAsync("listeners.selections", new() { ["id"] = id }))!;
        foreach (var item in (JsonArray)list["listeners"]!)
        {
            var selected = (JsonObject)item!; var binding = (JsonObject)selected["binding"]!;
            string listenerId = selected["id"]!.GetValue<string>();
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = selected["name"]!.GetValue<string>(), FontWeight = FontWeight.SemiBold });
            row.Children.Add(new TextBlock { Text = (binding["scheme"]?.GetValue<string>() ?? "http").ToUpperInvariant() + " · " + binding["address"]!.GetValue<string>() + ":" + binding["port"]!.GetValue<int>() +
                " · " + (binding["scope"]?.GetValue<string>() ?? "loopback"), TextWrapping = TextWrapping.Wrap });
            if (binding["publicBaseUrl"] is { } url) row.Children.Add(new TextBlock { Text = "Public address (unverified): " + url.GetValue<string>(), TextWrapping = TextWrapping.Wrap });
            var remove = new Button { Content = "Remove access" };
            remove.Click += async (_, _) => await RunAsync(async () =>
            {
                await CallAsync("listeners.revoke", new() { ["id"] = id, ["expectedHash"] = hash, ["listenerId"] = listenerId });
                await RefreshSelectedStatusAsync(); await RefreshListenersAsync();
            });
            row.Children.Add(remove); fields.Children.Add(row);
        }
        if (fields.Children.Count == 0) fields.Children.Add(new TextBlock { Text = "No listeners approved.", Opacity = .7 });
        if (client?.ServerInfo["listenerTlsAvailable"]?.GetValue<bool>() == true)
        {
            var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open listener settings.");
            var import = new Button { Content = "Import HTTPS certificate", Margin = new Thickness(0, 8, 0, 0) };
            import.Click += async (_, _) => await RunAsync(async () => await ImportListenerCertificateAsync(owner, id, hash));
            fields.Children.Add(import);
            var certificates = (JsonObject)(await CallAsync("listeners.certificates", new() { ["id"] = id }))!;
            foreach (var node in certificates["certificates"]!.AsArray())
            {
                var certificate = (JsonObject)node!; string certificateId = certificate["id"]!.GetValue<string>();
                var row = new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0) };
                row.Children.Add(new TextBlock { Text = certificate["name"]!.GetValue<string>() + " · " +
                    (certificate["notAfter"] is { } expires ? "expires " + expires.GetValue<string>() : "unavailable"), TextWrapping = TextWrapping.Wrap });
                var replace = new Button { Content = "Replace certificate" };
                replace.Click += async (_, _) => await RunAsync(async () => await ImportListenerCertificateAsync(owner, id, hash, certificateId));
                row.Children.Add(replace);
                var remove = new Button { Content = "Remove certificate" };
                remove.Click += async (_, _) => await RunAsync(async () =>
                {
                    await CallAsync("listeners.removeCertificate", new() { ["id"] = id, ["expectedHash"] = hash, ["certificateId"] = certificateId });
                    await RefreshListenersAsync();
                });
                row.Children.Add(remove); fields.Children.Add(row);
            }
        }
    }
    private async void ApproveListenerClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SelectedId is not string id || selectedHash is not string hash) return;
        var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open listener settings.");
        var content = NetworkPanel("Choose where clients can connect", id);
        var name = new TextBox { Name = "ListenerName", Text = "Local addon listener", MaxLength = 100 };
        var port = new TextBox { Name = "ListenerPort", Text = "7888", MaxLength = 5 };
        content.Children.Add(new TextBlock { Text = "Name" }); content.Children.Add(name);
        content.Children.Add(new TextBlock { Text = "Port (use the port in the addon's instructions)" }); content.Children.Add(port);
        var scope = new ComboBox { ItemsSource = new[] { "This computer only", "Local network", "Internet / public clients" }, SelectedIndex = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var address = new TextBox { Text = "127.0.0.1", MaxLength = 64 };
        var hosts = new TextBox { PlaceholderText = "Hostnames or IP addresses clients will use", MaxLength = 4096 };
        var publicUrl = new TextBox { PlaceholderText = "Optional: https://your-host.example:port", MaxLength = 512 };
        content.Children.Add(new TextBlock { Text = "Who may connect" }); content.Children.Add(scope);
        var networkOptions = new StackPanel { Spacing = 8, IsVisible = false };
        networkOptions.Children.Add(new TextBlock { Text = "Bind IP (0.0.0.0 listens on all IPv4 interfaces)" }); networkOptions.Children.Add(address);
        networkOptions.Children.Add(new TextBlock { Text = "Allowed hostnames / IPs (comma-separated, no scheme or port)" }); networkOptions.Children.Add(hosts);
        networkOptions.Children.Add(new TextBlock { Text = "Public address, if configured" }); networkOptions.Children.Add(publicUrl);
        networkOptions.Children.Add(new TextBlock { Text = "A successful bind does not prove other devices can connect. AJN does not change firewall or router settings. The addon must authenticate its clients.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(networkOptions);
        scope.SelectionChanged += (_, _) => { networkOptions.IsVisible = scope.SelectedIndex != 0; address.Text = scope.SelectedIndex == 0 ? "127.0.0.1" : "0.0.0.0"; };
        var scheme = new ComboBox { ItemsSource = new[] { "HTTP", "HTTPS" }, SelectedIndex = 0, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var certificateList = client?.ServerInfo["listenerTlsAvailable"]?.GetValue<bool>() == true
            ? ((JsonObject)(await CallAsync("listeners.certificates", new() { ["id"] = id }))!)["certificates"]!.AsArray() : new JsonArray();
        var certificate = new ComboBox { ItemsSource = certificateList.Select(c => c!["name"]!.GetValue<string>()).ToArray(), SelectedIndex = certificateList.Count > 0 ? 0 : -1, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch };
        var certificateHost = new TextBox { PlaceholderText = "Hostname covered by the certificate", MaxLength = 253 };
        var tls = new StackPanel { Spacing = 8, IsVisible = false };
        tls.Children.Add(new TextBlock { Text = "Import a PFX/PKCS#12 certificate from Listening access first. Its private key stays in AJN's protected storage.", TextWrapping = TextWrapping.Wrap });
        tls.Children.Add(certificate); tls.Children.Add(certificateHost);
        content.Children.Add(new TextBlock { Text = "Connection encryption" }); content.Children.Add(scheme); content.Children.Add(tls);
        scheme.SelectionChanged += (_, _) => tls.IsVisible = scheme.SelectedIndex == 1;
        var headers = new TextBox { Name = "ListenerSensitiveHeaders", MaxLength = 2048, PlaceholderText = "Comma-separated header names" };
        var query = new TextBox { Name = "ListenerSensitiveQuery", MaxLength = 2048, PlaceholderText = "Comma-separated query field names" };
        var advanced = new StackPanel { Spacing = 8 };
        advanced.Children.Add(new TextBlock { Text = "Optional fields specified by the addon creator. Their values are hidden from request events. Authorization and cookies are always hidden.", TextWrapping = TextWrapping.Wrap });
        advanced.Children.Add(headers); advanced.Children.Add(query);
        content.Children.Add(new Expander { Header = "Sensitive request fields", Content = advanced });
        var origins = new TextBox { PlaceholderText = "Exact origins, e.g. https://client.example", MaxLength = 8192 };
        var corsHeaders = new TextBox { PlaceholderText = "Allowed request headers, comma-separated", MaxLength = 2048 };
        var corsMethods = new TextBox { Text = "GET, HEAD, OPTIONS", MaxLength = 128 };
        var corsExpose = new TextBox { PlaceholderText = "Response headers the browser may read", MaxLength = 2048 };
        var corsCredentials = new CheckBox { Content = "Allow browser credentials for these exact origins" };
        var corsPanel = new StackPanel { Spacing = 8 };
        corsPanel.Children.Add(new TextBlock { Text = "Leave blank unless the addon needs browser access. Origins must be exact HTTP(S) addresses; wildcards are not supported.", TextWrapping = TextWrapping.Wrap });
        corsPanel.Children.Add(origins);
        corsPanel.Children.Add(new TextBlock { Text = "Allowed methods" }); corsPanel.Children.Add(corsMethods);
        corsPanel.Children.Add(corsHeaders); corsPanel.Children.Add(corsExpose); corsPanel.Children.Add(corsCredentials);
        content.Children.Add(new Expander { Header = "Browser access (CORS)", Content = corsPanel });
        if (!await ConsentAsync(owner, content, "Review listener")) return;
        if (!int.TryParse(port.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedPort) || parsedPort is < 1 or > 65535)
            throw new IOException("Enter a port number from 1 to 65535.");
        JsonArray Names(string? text) => new((text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(n => (JsonNode?)JsonValue.Create(n)).ToArray());
        if (scheme.SelectedIndex == 1 && certificate.SelectedIndex < 0) throw new IOException("Import an HTTPS certificate from Listening access, then add the listener.");
        var options = new JsonObject
        {
            ["scope"] = scope.SelectedIndex switch { 1 => "lan", 2 => "public", _ => "loopback" },
            ["scheme"] = scheme.SelectedIndex == 1 ? "https" : "http", ["address"] = address.Text,
            ["allowedHosts"] = Names(hosts.Text).Count > 0 ? Names(hosts.Text) : null,
            ["publicBaseUrl"] = string.IsNullOrWhiteSpace(publicUrl.Text) ? null : publicUrl.Text!.Trim(),
            ["certificateId"] = scheme.SelectedIndex == 1 ? certificateList[certificate.SelectedIndex]!["id"]!.GetValue<string>() : null,
            ["certificateHost"] = scheme.SelectedIndex == 1 ? certificateHost.Text?.Trim() : null,
            ["cors"] = Names(origins.Text).Count == 0 ? null : new JsonObject { ["origins"] = Names(origins.Text), ["methods"] = Names(corsMethods.Text),
                ["headers"] = Names(corsHeaders.Text), ["exposeHeaders"] = Names(corsExpose.Text), ["allowCredentials"] = corsCredentials.IsChecked == true }
        };
        await ApproveListenerAsync(owner, id, hash, name.Text ?? "", parsedPort, Names(headers.Text), Names(query.Text), options);
    });
    internal async Task ApproveListenerAsync(Window owner, string id, string hash, string name, int port, JsonArray headers, JsonArray query, JsonObject? options = null)
    {
        var parameters = new JsonObject
        {
            ["id"] = id, ["expectedHash"] = hash, ["address"] = "127.0.0.1", ["port"] = port,
            ["sensitiveHeaders"] = headers.DeepClone(), ["sensitiveQuery"] = query.DeepClone(),
        };
        foreach (var option in options ?? new JsonObject()) parameters[option.Key] = option.Value?.DeepClone();
        var review = (JsonObject)(await CallAsync("listeners.inspect", parameters))!;
        var binding = (JsonObject)review["binding"]!;
        bool local = (binding["scope"]?.GetValue<string>() ?? "loopback") == "loopback";
        var content = NetworkPanel(local ? "Approve this local listener?" : "Approve access from other computers?", id);
        content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = (binding["scheme"]?.GetValue<string>() ?? "http").ToUpperInvariant() + " at " + binding["address"]!.GetValue<string>() + ":" + binding["port"]!.GetValue<int>(), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = local ? "Only applications on this computer can connect. Use the addon's action to open this listener."
            : "Access: " + binding["scope"]!.GetValue<string>() + ". Other computers may reach this port. The addon must authenticate every client. No firewall or router settings will be changed.", TextWrapping = TextWrapping.Wrap });
        if (binding["allowedHosts"] is JsonArray allowed) content.Children.Add(new TextBlock { Text = "Allowed hostnames: " + string.Join(", ", allowed.Select(h => h!.GetValue<string>())), TextWrapping = TextWrapping.Wrap });
        if (binding["publicBaseUrl"] is { } url) content.Children.Add(new TextBlock { Text = "Public address (unverified): " + url.GetValue<string>(), TextWrapping = TextWrapping.Wrap });
        if (review["certificate"] is JsonObject cert && cert["fingerprint"] is not null)
            content.Children.Add(new TextBlock { Text = "Certificate: " + cert["name"]!.GetValue<string>() + "\nCovers: " + cert["hostname"]!.GetValue<string>() + "\nExpires: " + cert["notAfter"]!.GetValue<string>() + "\nSHA-256: " + cert["fingerprint"]!.GetValue<string>(), TextWrapping = TextWrapping.Wrap });
        if (binding["cors"] is JsonObject cors) content.Children.Add(new TextBlock { Text = "Browser origins: " + string.Join(", ", cors["origins"]!.AsArray().Select(o => o!.GetValue<string>())) +
            "\nMethods: " + string.Join(", ", cors["methods"]!.AsArray().Select(o => o!.GetValue<string>())) +
            "\nAllowed request headers: " + string.Join(", ", cors["headers"]!.AsArray().Select(o => o!.GetValue<string>())) +
            "\nExposed response headers: " + string.Join(", ", cors["exposeHeaders"]!.AsArray().Select(o => o!.GetValue<string>())) +
            "\nBrowser credentials: " + (cors["allowCredentials"]?.GetValue<bool>() == true ? "allowed" : "not allowed"), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Additional hidden headers: " + string.Join(", ", binding["sensitiveHeaders"]!.AsArray().Select(n => n!.GetValue<string>())) +
            "\nHidden query fields: " + string.Join(", ", binding["sensitiveQuery"]!.AsArray().Select(n => n!.GetValue<string>())), TextWrapping = TextWrapping.Wrap });
        if (!await ConsentAsync(owner, content, local ? "Allow local listener" : "Allow network listener")) return;
        await CallAsync("listeners.approve", new() { ["id"] = id, ["expectedHash"] = hash, ["name"] = name, ["reviewId"] = review["reviewId"]!.DeepClone() });
        await RefreshListenersAsync();
    }
    private async Task ImportListenerCertificateAsync(Window owner, string id, string hash, string? replaceId = null)
    {
        var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose a certificate with its private key", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("PFX / PKCS#12 certificate") { Patterns = new[] { "*.pfx", "*.p12" } } }
        });
        if (picked.Count == 0) return;
        string path = picked[0].TryGetLocalPath() ?? throw new IOException("Select a local certificate file.");
        var content = NetworkPanel(replaceId is null ? "Import HTTPS certificate" : "Replace HTTPS certificate", id);
        var name = new TextBox { Text = Path.GetFileNameWithoutExtension(path), MaxLength = 100 };
        var password = new TextBox { PasswordChar = '●', MaxLength = 4096 };
        content.Children.Add(new TextBlock { Text = "Name" }); content.Children.Add(name);
        content.Children.Add(new TextBlock { Text = "PFX password (leave blank if none)" }); content.Children.Add(password);
        content.Children.Add(new TextBlock { Text = replaceId is null ? "AJN encrypts the private key for this Windows user. Importing does not make the certificate trusted by clients."
            : "Replacement stops this addon and its listeners. Start the addon again after replacement to use the new certificate. It must still cover the listener's configured hostname.", TextWrapping = TextWrapping.Wrap });
        try
        {
            if (!await ConsentAsync(owner, content, replaceId is null ? "Import certificate" : "Replace and stop addon")) return;
            await CallAsync("listeners.importCertificate", new() { ["id"] = id, ["expectedHash"] = hash, ["path"] = path,
                ["password"] = password.Text ?? "", ["name"] = name.Text ?? "", ["replaceId"] = replaceId });
            await RefreshSelectedStatusAsync(); await RefreshListenersAsync();
        }
        finally { password.Text = ""; }
    }
}
