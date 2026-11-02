using AnimeJaNai.Addons.Management;
using AnimeJaNaiConfEditor.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views;

public partial class AddonsView
{
    internal void ProtectUnsavedSettingsOnClose(Window owner)
    {
        owner.Closed += async (_, _) => await CloseAsync();
        bool approved = false, confirming = false;
        owner.Closing += async (_, args) =>
        {
            if (approved) return;
            if (confirming) { args.Cancel = true; return; }
            // Another handler (for example diagnostics) can still cancel this
            // close. Dispose the connection only after the window has closed.
            if (!HasUnsavedSettings()) return;
            args.Cancel = true;
            confirming = true;
            try
            {
                if (await ConfirmAsync("Unsaved addon settings", "Your addon settings have not been saved. Cancel to keep editing, or close Manager and discard these changes.", "Discard and close", owner))
                { approved = true; owner.Close(); }
            }
            catch (Exception error) { ShowError(error); }
            finally { confirming = false; }
        };
    }

    internal static string ActionText(JsonNode? result)
    {
        if (result is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return text;
        if (result is JsonObject { Count: 1 } obj && obj["message"] is JsonValue message && message.TryGetValue<string>(out var readable)) return readable;
        return result?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? "Action completed.";
    }

    private bool HasUnsavedSettings()
    {
        if (displayedId is null) return false;
        try
        {
            foreach (var (name, read) in readSettings)
                if (!JsonNode.DeepEquals(read(), savedValues[name])) return true;
            return false;
        }
        catch { return true; } // Invalid edits must not disappear during a reload.
    }

    private void ShowError(Exception error)
    {
        bool disconnected = client?.IsConnected != true;
        if (disconnected) refreshTimer.Stop();
        string message;
        if (error is PlatformNotSupportedException)
            message = "This addon preview currently runs on Windows.";
        else if (disconnected && (!File.Exists(HostPath) || !File.Exists(RuntimePath)))
            message = "Addon support files are missing. Extract the complete AJN addon preview into a new folder, then open its Manager.";
        else if (disconnected)
            message = "AJN addon support is unavailable. Choose Try again. Your installed addons and saved settings are kept.";
        else message = error is ManagementException failure ? failure.Code switch
        {
            "permission_denied" => "This addon needs permission for that feature. Install the same package again to review its permissions, and check its Processing access or Service and device access settings.",
            "capacity_exceeded" => "There is no room for more addon work. Stop some addon work and try again. Performance limits controls background processing capacity.",
            "integrity_mismatch" => "The addon package changed or is damaged. Select a fresh copy and review it again.",
            "invalid_package" or "invalid_manifest" or "invalid_module" => "This file is not a valid AJN addon package. Ask its creator for an .ajnaddon file built for this preview.",
            "incompatible_api" or "missing_capability" or "feature_unavailable" => "This feature is unavailable in this AJN build. Check the addon's requirements and use a compatible full AJN build.",
            "addon_fault" => "The addon stopped after an error. Review its recent messages, then choose Start addon to retry.",
            _ => error.Message,
        } : error.Message;
        Control<TextBlock>("Status").Text = message;
        Control<TextBox>("ErrorText").Text = (error is ManagementException coded ? coded.Code + ": " : "") + error.Message;
        Control<Expander>("ErrorDetails").IsVisible = true;
        Control<Expander>("ErrorDetails").IsExpanded = false;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string accept, Window? dialogOwner = null)
    {
        var owner = dialogOwner ?? TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open this confirmation.");
        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 16 };
        var dialog = new Window { Title = title, Width = 540, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        var confirm = new Button { Content = accept };
        cancel.Click += (_, _) => dialog.Close(false);
        confirm.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(confirm); panel.Children.Add(buttons);
        return await dialog.ShowDialog<bool>(owner);
    }

    private async void HelpClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open addon help.");
        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 14 };
        var dialog = new Window { Title = "Using addons", Width = 620, Height = 590,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        panel.Children.Add(new TextBlock { Text = "Using addons", FontSize = 22 });
        foreach (string paragraph in new[] {
            "1. Choose Install addon and select an .ajnaddon package. AJN starts its local addon support automatically.",
            "2. Review the permissions. Only checked permissions are granted. Some features also need a specific media file, saved profile, service or device approved in their access section.",
            "3. Choose Start addon before using its actions. Starting it manually keeps it running after Manager closes. Stop addon ends its work.",
            "4. Edit settings and choose Save settings before switching addons. Reload lets you discard unsaved edits.",
            "5. Install a newer package to update an addon. Previous version restores the last installed package and its grant. Saved settings and addon data are kept. Remove stops the addon and removes access, while keeping saved data for reinstallation.",
            "Windows startup is optional and off by default. Website installation and automatic updates are not included in this developer preview.",
        }) panel.Children.Add(new TextBlock { Text = paragraph, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var buttons = new WrapPanel();
        foreach (var (label, file) in new[] { ("User guide", "USER-GUIDE.html"), ("Create an addon", "CREATOR-GUIDE.html") })
        {
            var button = new Button { Content = label, Margin = new Avalonia.Thickness(0, 0, 8, 8) };
            button.Click += (_, _) =>
            {
                try
                {
                    string path = Path.Combine(MainWindowViewModel.RootDir, "addon-development", file);
                    if (!File.Exists(path)) throw new IOException("This build does not include the offline guides. Extract the complete addon preview to get them.");
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception error) { ShowError(error); dialog.Close(); }
            };
            buttons.Children.Add(button);
        }
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => dialog.Close(); buttons.Children.Add(close); panel.Children.Add(buttons);
        await dialog.ShowDialog(owner);
    }, announceSuccess: false);
}
