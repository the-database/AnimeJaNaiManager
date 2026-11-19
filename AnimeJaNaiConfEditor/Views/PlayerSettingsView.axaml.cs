using AnimeJaNaiConfEditor.ViewModels;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;

namespace AnimeJaNaiConfEditor.Views;

public partial class PlayerSettingsView : UserControl
{
    public PlayerSettingsView()
    {
        AvaloniaXamlLoader.Load(this);
        // Text settings save when the box loses focus; Enter saves too.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && e.Source is TextBox { Classes: var classes } box && classes.Contains("optionText"))
            {
                BindingOperations.GetBindingExpressionBase(box, TextBox.TextProperty)?.UpdateSource();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
    }

    // An editable list's menu of common values.
    private void ChoicesButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PlayerOptionViewModel item } button) return;
        var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        foreach (var choice in item.Choices)
        {
            var entry = new MenuItem { Header = choice.Label };
            entry.Click += (_, _) => item.Pick(choice);
            menu.Items.Add(entry);
        }
        menu.ShowAt(button);
    }

    private async void BrowseFolderClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: PlayerOptionViewModel item } || TopLevel.GetTopLevel(this) is not { } top) return;
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = item.Label, AllowMultiple = false });
        if (folders.Count != 0 && folders[0].TryGetLocalPath() is { } path) item.Text = path;
    }
}
