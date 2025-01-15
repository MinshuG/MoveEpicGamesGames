using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoveEpicGamesGames.Models;
using MoveEpicGamesGames.Services;
using MoveEpicGamesGames.Services.Compression;
using MoveEpicGamesGames.Utils;

namespace MoveEpicGamesGames.ViewModels;

public partial class ArchiveEntryViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _fullPath = string.Empty;

    [ObservableProperty]
    private long _size;

    [ObservableProperty]
    private string _sizeDisplay = string.Empty;

    public ArchiveEntryViewModel(string path, long size)
    {
        FullPath = path;
        Name = Path.GetFileName(path);
        Size = size;
        SizeDisplay = size >= 0 ? FileUtils.FormatSize(size) : "Directory";
    }
}

public partial class ArchiveExplorerViewModel : ViewModelBase
{
    [ObservableProperty]
    private ObservableCollection<ArchiveEntryViewModel> _entries = new();

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    private string _progressText = string.Empty;

    [ObservableProperty]
    private string _archivePath = string.Empty;

    [ObservableProperty]
    private bool _isArchiveLoaded;

    private IArchive? _archive;

    [RelayCommand]
    private async Task OpenArchive()
    {
        var files = await AppHelper.TopLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Epic Games Archive") 
                { 
                    Patterns = new[] { "*.epiczip", "*.epiclz4" } 
                }
            }
        });

        if (files.Count == 0) return;

        await LoadArchive(files[0].Path.LocalPath);
    }

    public async Task LoadArchive(string path)
    {
        try
        {
            IsBusy = true;
            ProgressText = "Loading archive...";
            
            var extension = Path.GetExtension(path);
            var compressionService = CompressionFactory.GetService(
                extension == ".epiczip" ? CompressionMethod.Zip : CompressionMethod.Lz4);

            _archive?.Dispose();
            _archive = compressionService.OpenArchive(path, ArchiveOpenMode.Decompress);
            
            Entries.Clear();
            ArchivePath = path;

            foreach (var entry in _archive.Entries)
            {
                var size = -1L; // Default size for directories
                try
                {
                    using var stream = _archive.GetEntry(entry);
                    size = stream.Length;
                }
                catch { /* Ignore errors, treat as directory */ }

                Entries.Add(new ArchiveEntryViewModel(entry, size));
            }

            IsArchiveLoaded = true;
        }
        catch (Exception ex)
        {
            await ShowError("Error", $"Failed to load archive: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressText = string.Empty;
            _archive?.Dispose();
        }
    }

    [RelayCommand]
    private async Task ExtractSelected()
    {
        if (_archive == null || !Entries.Any(e => e.IsSelected))
            return;

        var folder = await AppHelper.TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false
        });

        if (folder.Count == 0) return;

        try
        {
            IsBusy = true;
            var destinationDir = folder[0].Path.LocalPath;
            var selectedEntries = Entries.Where(e => e.IsSelected).ToList();
            var current = 0;

            foreach (var entry in selectedEntries)
            {
                current++;
                ProgressValue = (double)current / selectedEntries.Count;
                ProgressText = $"Extracting {entry.Name}...";

                var fullPath = Path.Combine(destinationDir, entry.FullPath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                
                if (entry.Size >= 0) // Not a directory
                {
                    _archive.ExtractToFile(entry.FullPath, fullPath);
                }
            }

            await ShowError("Success", "Selected files extracted successfully.");
        }
        catch (Exception ex)
        {
            await ShowError("Error", $"Failed to extract files: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }

    [RelayCommand]
    private async Task Extract()
    {
        if (_archive == null)
            return;

        var folder = await AppHelper.TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false
        });

        if (folder.Count == 0) return;

        try
        {
            IsBusy = true;
            var destinationDir = folder[0].Path.LocalPath;
            var current = 0;

            _archive.ExtractAll(destinationDir, (entry, bytesRead) =>
            {
                current++;
                ProgressValue = (double)current / Entries.Count;
                ProgressText = entry;
            });

            await ShowError("Success", "All files extracted successfully.");
        }
        catch (Exception ex)
        {
            await ShowError("Error", $"Failed to extract files: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            ProgressValue = 0;
            ProgressText = string.Empty;
        }
    }
    
    [RelayCommand]
    private void SelectAll()
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = true;
        }
    }

    [RelayCommand]
    private void DeselectAll()
    {
        foreach (var entry in Entries)
        {
            entry.IsSelected = false;
        }
    }

    private async Task ShowError(string title, string message)
    {
        var dialog = new FluentAvalonia.UI.Controls.ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "Ok"
        };
        await dialog.ShowAsync();
    }

    public void Dispose()
    {
        _archive?.Dispose();
    }
}
