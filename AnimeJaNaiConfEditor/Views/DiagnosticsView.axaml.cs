using AnimeJaNaiConfEditor.Services.Diagnostics;
using AnimeJaNaiConfEditor.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace AnimeJaNaiConfEditor.Views;

public partial class DiagnosticsView : UserControl
{
    private CancellationTokenSource? _finish;
    private Task? _operation;
    private SupportReport? _report;
    private string? _lastZip;
    private string _destination = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "AnimeJaNai Diagnostics");
    public bool IsBusy => _operation is { IsCompleted: false };
    private T Control<T>(string name) where T : Control => this.FindControl<T>(name)!;

    public DiagnosticsView()
    {
        AvaloniaXamlLoader.Load(this);
        Control<TextBlock>("Destination").Text = "Save reports to: " + _destination;
        if (!OperatingSystem.IsWindows())
        {
            Control<Button>("RecordButton").IsEnabled = false;
            Control<TextBlock>("Status").Text = "Live recording is available on Windows. You can still save a diagnostics snapshot.";
        }
    }

    private async void ChooseVideo(object? sender, RoutedEventArgs args)
    {
        var files = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFilePickerAsync(new() {
            Title = "Choose the video that shows the problem", AllowMultiple = false,
            FileTypeFilter = [new("Video") { Patterns = ["*.mkv", "*.mp4", "*.avi", "*.webm", "*.m2ts", "*.ts"] }, FilePickerFileTypes.All]
        });
        if (files.Count != 0) Control<TextBox>("MediaPath").Text = files[0].TryGetLocalPath();
    }
    private void ClearVideo(object? sender, RoutedEventArgs args) => Control<TextBox>("MediaPath").Text = "";
    private async void RecordProblem(object? sender, RoutedEventArgs args) => await RunAsync(record: true);
    private async void SaveSnapshot(object? sender, RoutedEventArgs args) => await RunAsync(record: false);
    private void FinishRecording(object? sender, RoutedEventArgs args)
    { Control<Button>("FinishButton").IsEnabled = false; _finish?.Cancel(); }
    private void MarkProblem(object? sender, RoutedEventArgs args)
    {
        _report?.Mark(Control<TextBox>("Notes").Text ?? "Problem visible now");
        Control<TextBlock>("MarkerStatus").Text = "Problem marked at " + DateTime.Now.ToString("HH:mm:ss") + ". You can mark more than one moment.";
    }

    private async Task RunAsync(bool record)
    {
        if (IsBusy) return;
        _operation = RunCoreAsync(record);
        await _operation;
    }

    private async Task RunCoreAsync(bool record)
    {
        SetBusy(true, record);
        Control<Border>("ResultPanel").IsVisible = false;
        Control<Button>("RetryButton").IsVisible = false;
        Control<TextBlock>("MarkerStatus").Text = "";
        _finish = new CancellationTokenSource();
        var progress = new Progress<string>(s => Control<TextBlock>("Status").Text = s);
        try
        {
            string? media = Control<TextBox>("MediaPath").Text;
            _report = new SupportReport(MainWindowViewModel.RootDir, MainWindowViewModel.DataDir,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnimeJaNai", "Diagnostics"),
                Control<TextBox>("Notes").Text ?? "", string.IsNullOrWhiteSpace(media) ? null : media);
            if (record)
            {
                try { await SupportCapture.RecordAsync(_report, progress, _finish.Token); }
                catch (Exception ex) { _report.Warnings.Add("Recording failed: " + ex.Message); }
            }
            SetBusy(true, false);
            await ExportAsync(progress);
        }
        catch (Exception ex)
        {
            Control<TextBlock>("Status").Text = "Could not save the report: " + ex.Message + " Choose another report folder and retry.";
            Control<Button>("RetryButton").IsVisible = _report != null;
        }
        finally { _finish.Dispose(); _finish = null; SetBusy(false, false); }
    }

    private async Task ExportAsync(IProgress<string> progress)
    {
        _lastZip = await _report!.SaveAsync(_destination, progress);
        Control<SelectableTextBlock>("ReportPath").Text = _lastZip;
        Control<Border>("ResultPanel").IsVisible = true;
        Control<Border>("ResultPanel").BringIntoView();
        Control<TextBlock>("Status").Text = "Saved. Send the ZIP with your bug report." +
            (_report.Warnings.Count == 0 ? "" : " Some data was unavailable or shortened; details are inside report.json.");
        Control<Button>("RetryButton").IsVisible = false;
    }

    private async void RetrySave(object? sender, RoutedEventArgs args)
    {
        if (IsBusy || _report == null) return;
        SetBusy(true, false);
        _operation = ExportAsync(new Progress<string>(s => Control<TextBlock>("Status").Text = s));
        try { await _operation; }
        catch (Exception ex) { Control<TextBlock>("Status").Text = "Could not save the report: " + ex.Message; }
        finally { SetBusy(false, false); }
    }

    private void SetBusy(bool busy, bool recording)
    {
        foreach (string name in new[] { "RecordButton", "SnapshotButton", "BrowseButton", "ClearButton", "DestinationButton", "RetryButton" }) Control<Button>(name).IsEnabled = !busy;
        Control<Button>("RecordButton").IsEnabled = !busy && OperatingSystem.IsWindows();
        Control<ProgressBar>("Progress").IsVisible = busy;
        foreach (string name in new[] { "MarkButton", "FinishButton" })
        { Control<Button>(name).IsVisible = recording; Control<Button>(name).IsEnabled = recording; }
    }

    public async Task FinishBeforeCloseAsync()
    { _finish?.Cancel(); if (_operation != null) { try { await _operation; } catch { /* Status already shows the error. */ } } }

    private async void ChangeDestination(object? sender, RoutedEventArgs args)
    {
        var folders = await TopLevel.GetTopLevel(this)!.StorageProvider.OpenFolderPickerAsync(new() { Title = "Choose report folder", AllowMultiple = false });
        if (folders.Count != 0 && folders[0].TryGetLocalPath() is { } path)
        { _destination = path; Control<TextBlock>("Destination").Text = "Save reports to: " + path; }
    }
    private void OpenReportFolder(object? sender, RoutedEventArgs args)
    {
        if (_lastZip == null) return;
        try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(_lastZip)!) { UseShellExecute = true }); }
        catch (Exception ex) { Control<TextBlock>("Status").Text = "Could not open the folder: " + ex.Message; }
    }
    private async void CopyReportPath(object? sender, RoutedEventArgs args)
    { if (_lastZip != null && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(_lastZip); }
}
