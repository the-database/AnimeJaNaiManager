using AnimeJaNaiConfEditor.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views;

public partial class AddonsView
{
    private string? selectedHash;
    private bool mediaPermission;
    private async Task RefreshMediaAsync()
    {
        var section = Control<StackPanel>("MediaSection");
        var fields = Control<StackPanel>("MediaFields"); fields.Children.Clear();
        section.IsVisible = mediaPermission;
        if (!mediaPermission || SelectedId is not string id) return;
        bool available = client?.ServerInfo["nativeMediaAvailable"]?.GetValue<bool>() == true;
        Control<Button>("ApproveSourceButton").IsVisible = available;
        Control<Button>("ApproveProfileButton").IsVisible = available;
        Control<TextBlock>("MediaNotice").Text = available
            ? "Only the files and saved profile snapshots listed here are available to this addon version. Removing access stops the addon and its sessions."
            : "This AJN build does not include addon video processing. Use the complete addon preview for this feature.";
        if (!available) return;
        var selected = (JsonObject)(await CallAsync("media.selections", new() { ["id"] = id }))!;
        string hash = selectedHash!;
        foreach (string kind in new[] { "source", "profile" })
            foreach (var value in (JsonArray)selected[kind == "source" ? "sources" : "profiles"]!)
            {
                string selectionId = value!["id"]!.GetValue<string>();
                var row = new StackPanel { Spacing = 4 };
                row.Children.Add(new TextBlock { Text = value["name"]!.GetValue<string>(), FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
                row.Children.Add(new TextBlock { Text = kind == "source" ? value["path"]!.GetValue<string>() :
                    value["backend"]!.GetValue<string>() + " · saved profile snapshot", TextWrapping = TextWrapping.Wrap, Opacity = .7 });
                var revoke = new Button { Content = "Remove access", HorizontalAlignment = HorizontalAlignment.Left };
                revoke.Click += async (_, _) => await RunAsync(async () =>
                {
                    await CallAsync("media.revoke", new() { ["id"] = id, ["expectedHash"] = hash, ["kind"] = kind, ["selectionId"] = selectionId });
                    await RefreshSelectedStatusAsync(); await RefreshMediaAsync();
                });
                row.Children.Add(revoke); fields.Children.Add(row);
            }
        if (fields.Children.Count == 0) fields.Children.Add(new TextBlock { Text = "No files or profiles approved.", Opacity = .7 });
    }

    private async void ApproveSourceClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SelectedId is not string id || selectedHash is not string hash) return;
        var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open the media picker.");
        var picked = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Allow this addon to use one local media or subtitle file", AllowMultiple = false,
            FileTypeFilter = new[] { new FilePickerFileType("Video or subtitles") { Patterns = new[] { "*.mkv", "*.webm", "*.mp4", "*.m4v", "*.mov", "*.avi", "*.ts", "*.mts", "*.m2ts", "*.srt", "*.ass", "*.ssa", "*.vtt" } } },
        });
        string? path = picked.FirstOrDefault()?.TryGetLocalPath(); if (path is null) return;
        await ApproveSourceAsync(owner, id, hash, path);
    });

    internal async Task ApproveSourceAsync(Window owner, string id, string hash, string path)
    {
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "Allow this addon to process a file?", FontSize = 20, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = id, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = path, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "The addon can use this file with an approved profile until you remove access. Other files are not included.", TextWrapping = TextWrapping.Wrap });
        if (!await ConsentAsync(owner, content, "Allow file")) return;
        await CallAsync("media.approveSource", new() { ["id"] = id, ["expectedHash"] = hash, ["path"] = path });
        await RefreshMediaAsync();
    }

    private async void ApproveProfileClick(object? sender, RoutedEventArgs e) => await RunAsync(async () =>
    {
        if (SelectedId is not string id || selectedHash is not string hash) return;
        var owner = TopLevel.GetTopLevel(this) as Window ?? throw new IOException("Could not open profile selection.");
        await ApproveProfileAsync(owner, id, hash);
    });

    internal async Task ApproveProfileAsync(Window owner, string id, string hash)
    {
        string configPath = Path.Combine(MainWindowViewModel.DataDir, "animejanai.conf");
        using var file = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 48 * 1024) throw new IOException("The saved profile file is too large for an addon snapshot.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true));
        string configuration = await reader.ReadToEndAsync();
        var content = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        content.Children.Add(new TextBlock { Text = "Allow a saved profile", FontSize = 20, FontWeight = FontWeight.SemiBold });
        content.Children.Add(new TextBlock { Text = id, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = "Choose a copy of your saved settings for this addon. Save any profile edits before creating this snapshot.", TextWrapping = TextWrapping.Wrap });
        var slots = new[] { (1002, "Balanced"), (1001, "Quality"), (1003, "Performance"), (1012, "Upscale then interpolate"), (1013, "Interpolate then upscale") }
            .Concat(Enumerable.Range(1, 9).Select(n => (n, "Saved slot " + n))).ToArray();
        var profile = new ComboBox { ItemsSource = slots.Select(s => s.Item2).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var backend = new ComboBox { ItemsSource = MainWindowViewModel.TrtOnDisk() ? new[] { "DirectML", "TensorRT" } : new[] { "DirectML" }, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var name = new TextBox { Name = "ProfileName", Text = "Balanced", MaxLength = 128 };
        profile.SelectionChanged += (_, _) => { if (profile.SelectedIndex >= 0) name.Text = slots[profile.SelectedIndex].Item2; };
        content.Children.Add(new TextBlock { Text = "Saved profile" }); content.Children.Add(profile);
        content.Children.Add(new TextBlock { Text = "Backend" }); content.Children.Add(backend);
        content.Children.Add(new TextBlock { Text = "Name shown to this addon" }); content.Children.Add(name);
        if (!await ConsentAsync(owner, content, "Allow profile")) return;
        await CallAsync("media.approveProfile", new() { ["id"] = id, ["expectedHash"] = hash, ["name"] = name.Text ?? "",
            ["slot"] = slots[profile.SelectedIndex].Item1, ["backend"] = backend.SelectedItem as string, ["configuration"] = configuration });
        await RefreshMediaAsync();
    }

    private static Task<bool> ConsentAsync(Window owner, StackPanel content, string approveText)
    {
        var dialog = new Window { Title = approveText, Width = 600, MaxHeight = 640, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new ScrollViewer { Content = content,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled } };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true }; cancel.Click += (_, _) => dialog.Close(false);
        var approve = new Button { Content = approveText }; approve.Click += (_, _) => dialog.Close(true);
        buttons.Children.Add(cancel); buttons.Children.Add(approve); content.Children.Add(buttons);
        return dialog.ShowDialog<bool>(owner);
    }
}
