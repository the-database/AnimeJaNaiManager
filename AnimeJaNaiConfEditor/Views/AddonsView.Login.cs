using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.IO;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views;

public partial class AddonsView
{
    private async void LoginSettingsClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open login settings.");
        var current = await CallAsync("host.login");
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        var dialog = new Window { Title = "Addon Windows startup", Width = 570, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        panel.Children.Add(new TextBlock { Text = "Start background addons at sign-in", FontSize = 20, FontWeight = FontWeight.SemiBold });
        var enabled = new CheckBox { Name = "LoginEnabled", Content = "Start addons when I sign in to Windows", IsChecked = current!["enabled"]!.GetValue<bool>(),
            IsEnabled = current["available"]!.GetValue<bool>() };
        panel.Children.Add(enabled);
        panel.Children.Add(new TextBlock { Text = "Addons that support sign-in activation will start on your next Windows sign-in. Background addons can run without a player open. This setting applies to this AJN installation and your Windows account.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Turning this off releases sign-in activation now. Addons being used by a player or started manually keep running.", TextWrapping = TextWrapping.Wrap });
        if (!enabled.IsEnabled) panel.Children.Add(new TextBlock { Text = "The login launcher is missing from this build. Install a complete addon preview to enable startup.", TextWrapping = TextWrapping.Wrap });
        if (current["registeredElsewhere"]?.GetValue<bool>() == true) panel.Children.Add(new TextBlock { Text = "The startup entry for this data folder points to another installation. Saving with startup enabled will replace it with this installation; saving with it off removes the old entry.", TextWrapping = TextWrapping.Wrap });
        var errorText = new TextBlock { Name = "LoginSettingsError", IsVisible = false, TextWrapping = TextWrapping.Wrap }; panel.Children.Add(errorText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel" }; var save = new Button { Content = "Save" };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            save.IsEnabled = false; cancel.IsEnabled = false;
            try
            {
                await CallAsync("host.configureLogin", new() { ["enabled"] = enabled.IsChecked == true });
                Control<TextBlock>("Status").Text = enabled.IsChecked == true ? "Addon startup enabled for your next Windows sign-in." : "Addon sign-in startup disabled.";
                dialog.Close();
            }
            catch (Exception error) { errorText.Text = error.Message; errorText.IsVisible = true; }
            finally { save.IsEnabled = true; cancel.IsEnabled = true; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        await dialog.ShowDialog(owner);
    }, announceSuccess: false);
}
