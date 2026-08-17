using System.Collections.ObjectModel;
using GpuZip.Core;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace GpuZip.App;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<string> _inputs = [];
    private readonly ObservableCollection<ArchiveRow> _entries = [];
    private readonly SevenZipService _sevenZip = new();
    private string? _currentArchive;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        InputList.ItemsSource = _inputs;
        ArchiveList.ItemsSource = _entries;
        ConfigureWindow();
        _ = InitializeStatusAsync();
    }

    private void ConfigureWindow()
    {
        var handle = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(handle);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new SizeInt32(1240, 780));
        if (AppWindowTitleBar.IsCustomizationSupported())
        {
            appWindow.TitleBar.ExtendsContentIntoTitleBar = true;
            appWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
            appWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        }
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
    }

    private async Task InitializeStatusAsync()
    {
        var cuda = await Task.Run(GpuZipArchive.GetCudaDeviceInfo);
        CudaStatusText.Text = cuda.Available ? $"CUDA · {cuda.Name}" : "CPU fallback";
        CudaToggle.IsEnabled = cuda.Available;
        CudaToggle.IsChecked = cuda.Available;
        try
        {
            DetailText.Text = _sevenZip.IsAvailable ? await _sevenZip.VersionAsync() : "Bundled 7-Zip engine not found";
        }
        catch (Exception ex) { DetailText.Text = ex.Message; }
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        await OpenArchiveAsync(file.Path);
    }

    private async Task OpenArchiveAsync(string path)
    {
        await RunBusyAsync("Opening archive", async () =>
        {
            var entries = await LoadArchiveEntriesAsync(path);
            return $"Opened {Path.GetFileName(path)}";
        });
    }

    private async Task<IReadOnlyList<ArchiveEntryInfo>> LoadArchiveEntriesAsync(string path)
    {
        var entries = GpuZipArchive.IsGpuZip(path)
            ? await Task.Run(() => GpuZipArchive.List(path))
            : await _sevenZip.ListAsync(path);
        _entries.Clear();
        foreach (var entry in entries) _entries.Add(new(entry));
        _currentArchive = path;
        ArchiveTitle.Text = Path.GetFileName(path);
        ArchiveSummary.Text = $"{entries.Count:N0} entries";
        return entries;
    }

    private async void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var files = await picker.PickMultipleFilesAsync();
        foreach (var file in files.Where(file => !_inputs.Contains(file.Path))) _inputs.Add(file.Path);
        StatusText.Text = $"{_inputs.Count} input items selected";
    }

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !_inputs.Contains(folder.Path)) _inputs.Add(folder.Path);
        StatusText.Text = $"{_inputs.Count} input items selected";
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in InputList.SelectedItems.Cast<string>().ToList()) _inputs.Remove(item);
    }

    private async void CreateArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_inputs.Count == 0) { await ShowMessageAsync("No input", "Add files or a folder first."); return; }
        var item = (ComboBoxItem)FormatComboBox.SelectedItem;
        var format = item.Tag?.ToString() ?? "gpuz";
        var extension = format switch { "gpuz" => ".gpuz", "7z" => ".7z", "zip" => ".zip", "tar" => ".tar", "gzip" => ".gz", "bzip2" => ".bz2", "xz" => ".xz", _ => ".archive" };
        var picker = new FileSavePicker { SuggestedFileName = "Archive" };
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add(item.Content.ToString(), [extension]);
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        await RunBusyAsync("Creating archive", async () =>
        {
            ArchiveOperationResult result;
            if (format == "gpuz")
            {
                var progress = new Progress<ArchiveProgress>(UpdateProgress);
                result = await GpuZipArchive.CreateAsync(file.Path, _inputs, new()
                {
                    UseCuda = CudaToggle.IsChecked == true,
                    ThoroughSearch = true,
                    BlockSize = 4 * 1024 * 1024,
                    BrotliQuality = 11
                }, progress);
            }
            else
            {
                result = await _sevenZip.CreateAsync(file.Path, _inputs, format);
            }
            DetailText.Text = $"{ArchiveRow.FormatSize(result.InputBytes)} → {ArchiveRow.FormatSize(result.OutputBytes)} in {result.Elapsed.TotalSeconds:F2}s · CUDA {result.CudaUsed}";
            await LoadArchiveEntriesAsync(file.Path);
            return result.Summary;
        });
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_currentArchive is null) { await ShowMessageAsync("No archive", "Open an archive first."); return; }
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;
        await RunBusyAsync("Extracting", async () =>
        {
            var result = GpuZipArchive.IsGpuZip(_currentArchive)
                ? await GpuZipArchive.ExtractAsync(_currentArchive, folder.Path, new Progress<ArchiveProgress>(UpdateProgress))
                : await _sevenZip.ExtractAsync(_currentArchive, folder.Path);
            return result.Summary;
        });
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_currentArchive is null) { await ShowMessageAsync("No archive", "Open an archive first."); return; }
        await RunBusyAsync("Testing archive", async () =>
        {
            var result = GpuZipArchive.IsGpuZip(_currentArchive)
                ? await GpuZipArchive.TestAsync(_currentArchive, new Progress<ArchiveProgress>(UpdateProgress))
                : await _sevenZip.TestAsync(_currentArchive);
            await ShowMessageAsync("Archive test passed", result.Summary);
            return result.Summary;
        });
    }

    private void UpdateProgress(ArchiveProgress progress)
    {
        StatusText.Text = $"{progress.Operation}: {progress.CurrentPath}";
        OperationProgress.Value = progress.TotalEntries == 0 ? 0 : progress.CompletedEntries * 100.0 / progress.TotalEntries;
        DetailText.Text = $"{progress.CompletedEntries:N0} / {progress.TotalEntries:N0} entries · {ArchiveRow.FormatSize(progress.InputBytes)} read · {ArchiveRow.FormatSize(progress.OutputBytes)} written";
    }

    private async Task RunBusyAsync(string operation, Func<Task<string>> action)
    {
        if (_busy) return;
        _busy = true;
        StatusText.Text = operation;
        OperationProgress.IsIndeterminate = true;
        try
        {
            StatusText.Text = await action();
            OperationProgress.Value = 100;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{operation} failed";
            DetailText.Text = ex.Message;
            await ShowMessageAsync("Operation failed", ex.Message);
        }
        finally
        {
            OperationProgress.IsIndeterminate = false;
            _busy = false;
        }
    }

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = message, CloseButtonText = "OK", XamlRoot = Content.XamlRoot };
        await dialog.ShowAsync();
    }
}
