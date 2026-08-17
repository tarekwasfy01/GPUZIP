using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using GpuZip.Core;
using Microsoft.Win32;

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
        CudaCheck.IsChecked = true;
        MaximumPerformanceCheck.IsChecked = true;
        CudaStatusText.Text = "CUDA enabled · CPU fallback";
        DetailText.Text = _sevenZip.IsAvailable
            ? $"Bundled 7-Zip engine ready · {Environment.ProcessorCount} logical CPU cores"
            : "Bundled 7-Zip engine not found";
        App.LogStartup("Classic WPF file manager initialized; CUDA remains deferred until compression.");
    }

    private async void OpenArchive_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open archive",
            CheckFileExists = true,
            Filter = "Archive files|*.gpuz;*.7z;*.zip;*.rar;*.tar;*.gz;*.bz2;*.xz|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        await OpenArchiveAsync(dialog.FileName);
    }

    private async Task OpenArchiveAsync(string path)
    {
        await RunBusyAsync("Opening archive", async () =>
        {
            await LoadArchiveEntriesAsync(path);
            return $"Opened {Path.GetFileName(path)}";
        });
    }

    private async Task<IReadOnlyList<ArchiveEntryInfo>> LoadArchiveEntriesAsync(string path)
    {
        var entries = GpuZipArchive.IsGpuZip(path)
            ? await Task.Run(() => GpuZipArchive.List(path))
            : await _sevenZip.ListAsync(path);
        _entries.Clear();
        foreach (var entry in entries) _entries.Add(new ArchiveRow(entry));
        _currentArchive = path;
        ArchiveTitle.Text = path;
        ArchiveSummary.Text = $"{entries.Count:N0} entries";
        return entries;
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add files",
            CheckFileExists = true,
            Multiselect = true,
            Filter = "All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;
        foreach (var file in dialog.FileNames.Where(file => !_inputs.Contains(file))) _inputs.Add(file);
        StatusText.Text = $"{_inputs.Count} input items selected";
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var folder = PickFolder("Select a folder to add");
        if (folder is not null && !_inputs.Contains(folder)) _inputs.Add(folder);
        StatusText.Text = $"{_inputs.Count} input items selected";
    }

    private void RemoveSelected_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in InputList.SelectedItems.Cast<string>().ToList()) _inputs.Remove(item);
    }

    private async void CreateArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_inputs.Count == 0)
        {
            await ShowMessageAsync("No input", "Add files or a folder first.", true);
            return;
        }

        var item = FormatComboBox.SelectedItem as ComboBoxItem;
        var format = item?.Tag?.ToString() ?? "gpuz";
        var extension = format switch
        {
            "gpuz" => ".gpuz",
            "7z" => ".7z",
            "zip" => ".zip",
            "tar" => ".tar",
            "gzip" => ".gz",
            "bzip2" => ".bz2",
            "xz" => ".xz",
            _ => ".archive"
        };

        var label = item?.Content?.ToString() ?? format;
        var dialog = new SaveFileDialog
        {
            Title = "Create archive",
            FileName = "Archive",
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            Filter = $"{label}|*{extension}|All files|*.*"
        };
        if (dialog.ShowDialog(this) != true) return;

        await RunBusyAsync("Creating archive", async () =>
        {
            var maximumPerformance = MaximumPerformanceCheck.IsChecked == true;
            var process = Process.GetCurrentProcess();
            ProcessPriorityClass? previousPriority = null;
            if (maximumPerformance)
            {
                try
                {
                    previousPriority = process.PriorityClass;
                    process.PriorityClass = ProcessPriorityClass.High;
                }
                catch { }
            }

            try
            {
                ArchiveOperationResult result;
                if (format == "gpuz")
                {
                    DetailText.Text = maximumPerformance
                        ? $"Maximum performance · {Environment.ProcessorCount} CPU workers requested · CUDA {(CudaCheck.IsChecked == true ? "enabled" : "disabled")}" 
                        : "Balanced compression mode";

                    var progress = new Progress<ArchiveProgress>(UpdateProgress);
                    result = await GpuZipArchive.CreateAsync(dialog.FileName, _inputs, new GpuZipCreateOptions
                    {
                        UseCuda = CudaCheck.IsChecked == true,
                        ThoroughSearch = true,
                        MaximumPerformance = maximumPerformance,
                        MaxParallelism = maximumPerformance ? Environment.ProcessorCount : 1,
                        BlockSize = 4 * 1024 * 1024,
                        BrotliQuality = 11
                    }, progress);
                }
                else
                {
                    result = await _sevenZip.CreateAsync(dialog.FileName, _inputs, format);
                }

                DetailText.Text = $"{ArchiveRow.FormatSize(result.InputBytes)} → {ArchiveRow.FormatSize(result.OutputBytes)} in {result.Elapsed.TotalSeconds:F2}s · CUDA {result.CudaUsed}";
                await LoadArchiveEntriesAsync(dialog.FileName);
                return result.Summary;
            }
            finally
            {
                if (previousPriority.HasValue)
                {
                    try { process.PriorityClass = previousPriority.Value; } catch { }
                }
            }
        });
    }

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_currentArchive is null)
        {
            await ShowMessageAsync("No archive", "Open an archive first.", true);
            return;
        }

        var folder = PickFolder("Select extraction destination");
        if (folder is null) return;
        await RunBusyAsync("Extracting", async () =>
        {
            var result = GpuZipArchive.IsGpuZip(_currentArchive)
                ? await GpuZipArchive.ExtractAsync(_currentArchive, folder, new Progress<ArchiveProgress>(UpdateProgress))
                : await _sevenZip.ExtractAsync(_currentArchive, folder);
            return result.Summary;
        });
    }

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_currentArchive is null)
        {
            await ShowMessageAsync("No archive", "Open an archive first.", true);
            return;
        }

        await RunBusyAsync("Testing archive", async () =>
        {
            var result = GpuZipArchive.IsGpuZip(_currentArchive)
                ? await GpuZipArchive.TestAsync(_currentArchive, new Progress<ArchiveProgress>(UpdateProgress))
                : await _sevenZip.TestAsync(_currentArchive);
            await ShowMessageAsync("Archive test passed", result.Summary, false);
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
        OperationProgress.Value = 0;
        OperationProgress.IsIndeterminate = true;
        try
        {
            StatusText.Text = await action();
            OperationProgress.IsIndeterminate = false;
            OperationProgress.Value = 100;
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{operation} failed";
            DetailText.Text = ex.Message;
            App.LogStartup($"{operation} failed: {ex}");
            await ShowMessageAsync("Operation failed", ex.Message, true);
        }
        finally
        {
            OperationProgress.IsIndeterminate = false;
            _busy = false;
        }
    }

    private string? PickFolder(string title)
    {
        var dialog = new OpenFolderDialog
        {
            Title = title,
            Multiselect = false
        };
        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private Task ShowMessageAsync(string title, string message, bool error)
    {
        MessageBox.Show(this, message, title, MessageBoxButton.OK,
            error ? MessageBoxImage.Error : MessageBoxImage.Information);
        return Task.CompletedTask;
    }
}
