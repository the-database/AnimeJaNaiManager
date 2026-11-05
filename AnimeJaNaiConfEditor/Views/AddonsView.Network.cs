using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views;

public partial class AddonsView
{
    private bool networkPermission, credentialPermission, outputPermission, inputPermission;

    private async Task RefreshNetworkAsync()
    {
        var section = Control<StackPanel>("NetworkSection");
        var fields = Control<StackPanel>("NetworkFields"); fields.Children.Clear();
        section.IsVisible = networkPermission;
        if (!networkPermission || SelectedId is not string id || selectedHash is not string hash) return;
        bool available = client?.ServerInfo["networkAvailable"]?.GetValue<bool>() == true;
        Control<Button>("ApproveDestinationButton").IsVisible = available;
        Control<TextBlock>("NetworkNotice").Text = available
            ? "Only the destinations listed here are available to this addon version. Removing access or changing a credential stops the addon and its work."
            : "This AJN build does not include addon service and device access.";
        if (!available) return;
        var list = (JsonObject)(await CallAsync("network.selections", new() { ["id"] = id }))!;
        foreach (var item in (JsonArray)list["destinations"]!)
        {
            var selected = (JsonObject)item!;
            string destinationId = selected["id"]!.GetValue<string>(), origin = selected["origin"]!.GetValue<string>();
            string protocol = selected["protocol"]!.GetValue<string>();
            bool hasCredential = selected["hasCredential"]?.GetValue<bool>() == true;
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = selected["name"]!.GetValue<string>(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = origin, TextWrapping = TextWrapping.Wrap });
            row.Children.Add(new TextBlock { Text = "Approved addresses: " + string.Join(", ", ((JsonArray)selected["addresses"]!).Select(a => a!.GetValue<string>())), Opacity = .7, TextWrapping = TextWrapping.Wrap });
            if (hasCredential) row.Children.Add(new TextBlock { Text = "Saved credential: " + selected["credentialHeader"]!.GetValue<string>(), TextWrapping = TextWrapping.Wrap, Opacity = .7 });
            var buttons = new WrapPanel();
            var remove = new Button { Content = "Remove access", Margin = new Thickness(0, 0, 8, 4) };
            remove.Click += async (_, _) => await RunAsync(async () =>
            {
                await CallAsync("network.revoke", new() { ["id"] = id, ["expectedHash"] = hash, ["destinationId"] = destinationId });
                await RefreshSelectedStatusAsync(); await RefreshNetworkAsync();
            });
            buttons.Children.Add(remove);
            if (credentialPermission && client?.ServerInfo["credentialsAvailable"]?.GetValue<bool>() == true && protocol is "http" or "https")
            {
                var credential = new Button { Content = hasCredential ? "Replace credential" : "Save credential", Margin = new Thickness(0, 0, 8, 4) };
                credential.Click += async (_, _) => await RunAsync(async () =>
                {
                    var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open credential settings.");
                    await SetNetworkCredentialAsync(owner, id, hash, selected);
                });
                buttons.Children.Add(credential);
            }
            if (hasCredential)
            {
                var clear = new Button { Content = "Remove credential", Margin = new Thickness(0, 0, 8, 4) };
                clear.Click += async (_, _) => await RunAsync(async () =>
                {
                    await CallAsync("network.removeCredential", new() { ["id"] = id, ["expectedHash"] = hash, ["destinationId"] = destinationId });
                    await RefreshSelectedStatusAsync(); await RefreshNetworkAsync();
                });
                buttons.Children.Add(clear);
            }
            row.Children.Add(buttons); fields.Children.Add(row);
        }
        if (fields.Children.Count == 0) fields.Children.Add(new TextBlock { Text = "No services or devices approved.", Opacity = .7 });
    }

    private async void ApproveDestinationClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SelectedId is not string id || selectedHash is not string hash) return;
        var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open service settings.");
        var content = NetworkPanel("Choose a service or device", id);
        var name = new TextBox { Name = "DestinationName", MaxLength = 100, PlaceholderText = "Name shown to this addon" };
        var origin = new TextBox { Name = "DestinationOrigin", MaxLength = 512, PlaceholderText = "https://service.example or udp://192.168.1.20:21324" };
        content.Children.Add(new TextBlock { Text = "Name" }); content.Children.Add(name);
        content.Children.Add(new TextBlock { Text = "Service address" }); content.Children.Add(origin);
        content.Children.Add(new TextBlock { Text = "Use HTTP, HTTPS or UDP with a port. The next screen shows the exact addresses to approve. Leave paths and credentials out of the address.", TextWrapping = TextWrapping.Wrap });
        if (!await ConsentAsync(owner, content, "Review address")) return;
        await ApproveDestinationAsync(owner, id, hash, name.Text ?? "", origin.Text ?? "");
    });

    internal async Task ApproveDestinationAsync(Window owner, string id, string hash, string name, string origin)
    {
        var reviewed = (JsonObject)(await CallAsync("network.inspectDestination", new() { ["id"] = id, ["expectedHash"] = hash, ["origin"] = origin }))!;
        var content = NetworkPanel("Allow service access?", id);
        content.Children.Add(new TextBlock { Text = name, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = reviewed["origin"]!.GetValue<string>(), TextWrapping = TextWrapping.Wrap });
        string protocol = reviewed["protocol"]!.GetValue<string>();
        content.Children.Add(new TextBlock { Text = protocol == "udp"
            ? "This addon can send datagrams to this device and port. This does not grant listening or discovery."
            : "This addon can read responses and make changes through HTTP requests at all paths on this service. Redirects do not grant access to another service.", TextWrapping = TextWrapping.Wrap });
        if (outputPermission && protocol is "http" or "https") content.Children.Add(new TextBlock {
            Text = "This addon also has permission to send processed video and audio from approved files or media services to this service.",
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        if (inputPermission && protocol is "http" or "https") content.Children.Add(new TextBlock {
            Text = "This addon also has permission to read and process media from all paths on this service using the profiles you approve.",
            TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        if (protocol == "http") content.Children.Add(new TextBlock { Text = "HTTP traffic is unencrypted.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Approved addresses: " + string.Join(", ", ((JsonArray)reviewed["addresses"]!).Select(a => a!.GetValue<string>())), TextWrapping = TextWrapping.Wrap });
        if (((JsonArray)reviewed["addresses"]!).Any(n => System.Net.IPAddress.TryParse(n!.GetValue<string>(), out var ip) &&
            (System.Net.IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal ||
             ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes()[0] == 169 && ip.GetAddressBytes()[1] == 254)))
            content.Children.Add(new TextBlock { Text = "This address reaches a service on this PC or its local link. It may expose private device or cloud metadata. Approve it only if this is the service you intended.", TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = "These addresses remain fixed to this approval. Review the address again if the service moves. You can remove access from the addon page.", TextWrapping = TextWrapping.Wrap });
        if (!await ConsentAsync(owner, content, "Allow service")) return;
        await CallAsync("network.approveDestination", new() { ["id"] = id, ["expectedHash"] = hash, ["name"] = name, ["reviewId"] = reviewed["reviewId"]!.DeepClone() });
        await RefreshNetworkAsync();
    }

    internal async Task SetNetworkCredentialAsync(Window owner, string id, string hash, JsonObject selected)
    {
        var content = NetworkPanel("Save a service credential", id);
        content.Children.Add(new TextBlock { Text = selected["origin"]!.GetValue<string>(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var header = new TextBox { Name = "CredentialHeader", Text = selected["credentialHeader"]?.GetValue<string>() ?? "Authorization", MaxLength = 64 };
        var value = new TextBox { Name = "CredentialValue", PasswordChar = '●', MaxLength = 4096 };
        content.Children.Add(new TextBlock { Text = "Header name" }); content.Children.Add(header);
        content.Children.Add(new TextBlock { Text = "Complete header value (for example, Bearer followed by your token)" }); content.Children.Add(value);
        content.Children.Add(new TextBlock { Text = "AJN stores this credential for your Windows account and sends it only to this approved service when the addon requests it. Changing it stops the addon. The current value is never displayed.", TextWrapping = TextWrapping.Wrap });
        if (selected["protocol"]!.GetValue<string>() == "http") content.Children.Add(new TextBlock { Text = "This HTTP service receives the credential without encryption.", TextWrapping = TextWrapping.Wrap });
        try
        {
            if (!await ConsentAsync(owner, content, "Save credential")) return;
            await CallAsync("network.setCredential", new() { ["id"] = id, ["expectedHash"] = hash,
                ["destinationId"] = selected["id"]!.DeepClone(), ["header"] = header.Text ?? "", ["value"] = value.Text ?? "" });
            await RefreshSelectedStatusAsync(); await RefreshNetworkAsync();
        }
        finally { value.Text = ""; }
    }

    internal async Task<string?> ConnectDeviceAsync(Window owner, string id, string hash, string setting, string label, string host)
    {
        var reviewed = (JsonObject)(await CallAsync("network.inspectDevice", new() { ["id"] = id, ["expectedHash"] = hash,
            ["setting"] = setting, ["host"] = host }))!;
        var content = NetworkPanel("Connect " + label + "?", id);
        content.Children.Add(new TextBlock { Text = reviewed["host"]!.GetValue<string>(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        var destinations = (JsonArray)reviewed["destinations"]!;
        content.Children.Add(new TextBlock { Text = "Allow this addon to control the device using these services:", TextWrapping = TextWrapping.Wrap });
        foreach (var destination in destinations)
            content.Children.Add(new TextBlock { Text = destination!["origin"]!.GetValue<string>(), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "HTTP services allow reading and changing any path on the device. UDP services allow sending datagrams to the listed ports. HTTP traffic is unencrypted.", TextWrapping = TextWrapping.Wrap });
        if (outputPermission) content.Children.Add(new TextBlock { Text = "This addon can also send processed video and audio to the approved HTTP services.", TextWrapping = TextWrapping.Wrap });
        if (inputPermission) content.Children.Add(new TextBlock { Text = "This addon can also read and process media from the approved HTTP services.", TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Device addresses: " + string.Join(", ", ((JsonArray)destinations[0]!["addresses"]!).Select(a => a!.GetValue<string>())), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "This saves the address and grants access to all services above together. Reconnect here if the device's IP address changes. A running addon stops while its device access changes.", TextWrapping = TextWrapping.Wrap });
        if (!await ConsentAsync(owner, content, "Connect device")) return null;
        await CallAsync("network.approveDevice", new() { ["id"] = id, ["expectedHash"] = hash, ["reviewId"] = reviewed["reviewId"]!.DeepClone() });
        await RefreshSelectedStatusAsync(); await RefreshNetworkAsync();
        Control<TextBlock>("Status").Text = "Device address saved and access granted. Start the addon to use it. Other unsaved settings remain in the form.";
        return reviewed["host"]!.GetValue<string>();
    }

    private static StackPanel NetworkPanel(string title, string id)
    {
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = id, TextWrapping = TextWrapping.Wrap }); return content;
    }
}
