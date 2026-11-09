using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using System;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views;

public partial class AddonsView
{
    private async void HostSettingsClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open performance limits.");
        await EditHostSettingsAsync(owner);
    }, announceSuccess: false);

    internal async Task EditHostSettingsAsync(Window owner)
    {
        var current = await CallAsync("host.settings");
        bool editable = current!["editable"]!.GetValue<bool>();
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        var dialog = new Window { Title = "Addon performance limits", Width = 570, SizeToContent = SizeToContent.Height,
            CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        panel.Children.Add(new TextBlock { Text = "Addon processing capacity", FontSize = 20, FontWeight = FontWeight.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Maximum simultaneous processing sessions across all addons", TextWrapping = TextWrapping.Wrap });
        var limit = new TextBox { Name = "NativeSessionLimit", Text = current["maximumConcurrentSessions"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture),
            MaxLength = 3, IsReadOnly = !editable };
        panel.Children.Add(limit);
        panel.Children.Add(new TextBlock { Text = editable
            ? "Choose 1 to 16. Existing sessions continue if you lower this limit. New processing starts when there is room; each addon decides how many sessions to request."
            : "A developer launch option controls this limit. Close that development session and reopen AJN normally to use saved settings.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "More concurrent processing uses more GPU and system resources. This setting covers background addon sessions; it does not change your player's profile.", TextWrapping = TextWrapping.Wrap });
        var errorText = new TextBlock { Name = "HostSettingsError", IsVisible = false, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(errorText);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = editable ? "Cancel" : "Close" };
        var save = new Button { Content = "Save", IsVisible = editable };
        cancel.Click += (_, _) => dialog.Close();
        save.Click += async (_, _) =>
        {
            if (!int.TryParse(limit.Text?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value is < 1 or > 16)
            {
                errorText.Text = "Choose a whole number from 1 to 16."; errorText.IsVisible = true; return;
            }
            save.IsEnabled = false; cancel.IsEnabled = false;
            try
            {
                await CallAsync("host.configure", new() { ["maximumConcurrentSessions"] = value });
                Control<TextBlock>("Status").Text = "Performance limit saved. Existing sessions continue; new sessions use this limit.";
                dialog.Close();
            }
            catch (Exception error) { errorText.Text = error.Message; errorText.IsVisible = true; }
            finally { save.IsEnabled = true; cancel.IsEnabled = true; }
        };
        buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        await dialog.ShowDialog(owner);
    }
}
