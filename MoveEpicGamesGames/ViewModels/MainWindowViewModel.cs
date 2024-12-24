using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using FluentAvalonia.UI.Controls;
using Newtonsoft.Json;
using MoveEpicGamesGames.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MoveEpicGamesGames.Services;
using MoveEpicGamesGames.Utils;

namespace MoveEpicGamesGames.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private List<string> _availableGames = new();

        [NotifyPropertyChangedFor(nameof(CanMove))]
        [ObservableProperty]
        private string? _selectedGame;

        [NotifyPropertyChangedFor(nameof(CanMove))]
        [ObservableProperty]
        private string? _destinationFolder;
        
        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private bool _hasNoGames;

        [ObservableProperty]
        private string _destinationFolderDisplay = "No folder selected";

        [ObservableProperty]
        private double _progressValue;

        [ObservableProperty]
        private string _progressText = "";

        [ObservableProperty]
        private bool _showDebug;

        public bool CanMove => !string.IsNullOrEmpty(_selectedGame) && !string.IsNullOrEmpty(_destinationFolder);

        private List<GameManifest> _manifests = new();
        private Dictionary<string, string> _appNameToManifestPath = new();

    
        public MainWindowViewModel()
        {
            LoadGameEntries();
        }

        private async void LoadGameEntries()
        {
            try
            {
                string launcherInstalledPath = @"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat";
                using var file = File.OpenText(launcherInstalledPath);
                var data = JsonConvert.DeserializeObject<dynamic>(await file.ReadToEndAsync());
                var list = data["InstallationList"];
                var entries = new List<string>();

                string manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
                foreach (var manifestFile in Directory.GetFiles(manifestsPath, "*.item"))
                {
                    using var mf = File.OpenText(manifestFile);
                    var mData = JsonConvert.DeserializeObject<GameManifest>(await mf.ReadToEndAsync());
                    if (mData == null) continue;
                    mData.FilePath = manifestFile;
                    _manifests.Add(mData);
                    _appNameToManifestPath[mData.AppName] = manifestFile;
                }

                foreach (var game in list)
                {
                    string app = game["AppName"];
                    var mf = _manifests.FirstOrDefault(x => x.AppName == app);
                    if (mf != null)
                    {
                        if (mf.DisplayName == app) entries.Add(mf.DisplayName);
                        else entries.Add($"{mf.DisplayName} ({app})");
                    }
                }

                if (entries.Count == 0)
                {
                    HasNoGames = true;
                    return;
                }

                HasNoGames = false;
                AvailableGames = entries;
            }
            catch
            {
                HasNoGames = true;
                // Consider showing an error message here
            }
        }
        
        [RelayCommand]
        private async Task PickFolder()
        {
            var folder = await AppHelper.TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false
            });

            if (folder.Count > 0)
            {
                var path = folder[0].Path.LocalPath;
                if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                {
                    DestinationFolder = path;
                    DestinationFolderDisplay = Path.GetFullPath(path);
                }
            }
        }
    
        [RelayCommand]
        private async Task MoveSelectedGame()
        {
            if (string.IsNullOrEmpty(_selectedGame)) return;
            var manifest = FindManifest(_selectedGame);
            if (manifest == null) return;

            while (!await ProcessHelper.CheckAndWarnForEpicProcesses())
            {
                return;
            }

            // Check DLC and other validations...

            var dialog = new ContentDialog
            {
                Title = "Move",
                Content = $"Are you sure you want to move {manifest.DisplayName} from \"{new DirectoryInfo(manifest.InstallLocation).Parent}\" to \"{_destinationFolder}\"?",
                PrimaryButtonText = "Yes",
                CloseButtonText = "No"
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return;

            try
            {
                await ShowProgressOverlay(async progress =>
                {
                    var targetPath = Path.Combine(_destinationFolder!, Path.GetFileName(manifest.InstallLocation));
                    await MoveDirectoryWithProgress(manifest.InstallLocation, targetPath, progress);
                    UpdateManifestInstallLocation(manifest, _destinationFolder!);
                });

                await ShowError("Success", "Game moved successfully.");
            }
            catch (Exception ex)
            {
                var error = ex.Message;
                await ShowError("Error", $"Failed to move or update: {error}");
            }
        }

        private async Task MoveDirectoryWithProgress(string sourceDir, string targetDir, IProgress<(string, double)> progress)
        {
            // Calculate total size
            var totalSize = Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            long currentSize = 0;

            // Create all directories first
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var newDir = dir.Replace(sourceDir, targetDir);
                Directory.CreateDirectory(newDir);
            }

            // Move all files with progress
            foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                var newFile = file.Replace(sourceDir, targetDir);
                File.Move(file, newFile);
                
                currentSize += new FileInfo(newFile).Length;
                progress.Report(($"Moving: {Path.GetFileName(file)}", (double)currentSize / totalSize));
            }

            // Delete source directory if empty
            if (Directory.Exists(sourceDir) && !Directory.EnumerateFileSystemEntries(sourceDir).Any())
            {
                Directory.Delete(sourceDir, true);
            }
        }

        private async Task ShowProgressOverlay(Func<IProgress<(string Operation, double Progress)>, Task> operation)
        {
            IsBusy = true;
            ProgressValue = 0;
            ProgressText = "Preparing...";
            
            try
            {
                var progress = new Progress<(string Operation, double Progress)>(report =>
                {
                    ProgressText = report.Operation;
                    ProgressValue = report.Progress;
                });

                await Task.Run(() => operation(progress));
            }
            finally
            {
                IsBusy = false;
                ProgressValue = 0;
                ProgressText = "";
            }
        }

        [RelayCommand]
        private async Task BackupSelectedGame()
        {
            if (IsBusy) return;
            if (string.IsNullOrEmpty(_selectedGame)) return;
            var manifest = FindManifest(_selectedGame);
            if (manifest == null) return;

            var file = await AppHelper.TopLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                DefaultExtension = "epiczip",
                FileTypeChoices = new List<FilePickerFileType>
                {
                    new FilePickerFileType("Epic Games Backup Zip Archive") { Patterns = new[] { "*.epiczip" } }
                }
            });

            if (file == null) return;

            if (File.Exists(file.Path.LocalPath))
            {
                try
                {
                    File.Delete(file.Path.LocalPath);
                }
                catch (Exception e)
                {
                    await ShowError("Error", $"Failed to delete existing file: {e.Message}");
                    return;
                }
            }

            try
            {
                await ShowProgressOverlay(async progress =>
                {
                    await GameBackupService.BackupGameAsync(manifest, file.Path.LocalPath, progress);
                });
                
                await ShowError("Success", "Game backed up successfully.");
            }
            catch (Exception ex)
            {
                await ShowError("Error", $"Failed to backup game: {ex.Message}");
                try
                {
                    File.Delete(file.Path.LocalPath);
                }
                catch
                {
                    // ignored
                }
            }
        }

        [RelayCommand]
        private async Task RestoreBackup()
        {
            if (IsBusy) return;

            while (!await ProcessHelper.CheckAndWarnForEpicProcesses())
            {
                return;
            }

            var file = await AppHelper.TopLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                FileTypeFilter = new List<FilePickerFileType>
                {
                    new FilePickerFileType("Epic Games Backup Zip Archive") { Patterns = new[] { "*.epiczip" } }
                }
            });

            if (file == null || file.Count == 0) return;

            string archivePath = file[0].Path.LocalPath;
            var dialog = new ContentDialog
            {
                Title = "Restore Backup",
                Content = "Select a folder to install the backup to.",
                PrimaryButtonText = "OK",
                CloseButtonText = "Cancel"
            };

            var res = await dialog.ShowAsync();
            if (res != ContentDialogResult.Primary) return;

            var folderPicker = await AppHelper.TopLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false
            });
            if (folderPicker.Count == 0) return;

            var restoreLocation = folderPicker[0].Path.LocalPath;
            
            try
            {
                await ShowProgressOverlay(async progress =>
                {
                    await GameBackupService.RestoreBackupAsync(archivePath, restoreLocation, progress);
                });
                
                await ShowError("Success", "Backup restored successfully.");
            }
            catch (Exception ex)
            {
                await ShowError("Error", $"Failed to restore backup: {ex.Message}");
            }
        }

        [RelayCommand]
        private void ToggleDebug() => ShowDebug = !ShowDebug;

        [RelayCommand]
        private void DebugToggleIsBusy()
        {
            IsBusy = !IsBusy;
            if (IsBusy)
            {
                ProgressValue = 0.5;
                ProgressText = "Debug Progress";
            }
            else
            {
                ProgressValue = 0;
                ProgressText = "";
            }
        }

        private async Task ShowError(string title, string msg)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = msg,
                CloseButtonText = "Ok"
            };
            await dialog.ShowAsync();
        }

        private GameManifest? FindManifest(string selectedDisplay)
        {
            var realAppName = _manifests.FirstOrDefault(x =>
                selectedDisplay.Contains(x.AppName) ||
                selectedDisplay == x.DisplayName)?.AppName;

            return _manifests.FirstOrDefault(x => x.AppName == realAppName);
        }

        private void UpdateManifestInstallLocation(GameManifest manifest, string newBase)
        {
            var original = Path.GetFullPath(manifest.InstallLocation);
            var newInstall = Path.Combine(newBase, Path.GetFileName(original));
            manifest.InstallLocation = newInstall;

            // Update ManifestLocation
            if (IsSubdir(original, manifest.ManifestLocation))
            {
                var relManifest = Path.GetRelativePath(original, manifest.ManifestLocation);
                manifest.ManifestLocation = Path.Combine(newInstall, relManifest);
            }
            else
            {
                // TODO:
                // move and update manifest file?
            }

            // Update StagingLocation
            if (IsSubdir(original, manifest.StagingLocation))
            {
                var relStaging = Path.GetRelativePath(original, manifest.StagingLocation);
                manifest.StagingLocation = Path.Combine(newInstall, relStaging);
            }
            else
            {
                // TODO: same as above
            }
            
            // Update launcher file
            var launcherInstalledPath = @"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat";
            File.Copy(launcherInstalledPath, launcherInstalledPath + ".bak", true);

            var data = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(launcherInstalledPath));
            foreach (var game in data["InstallationList"])
            {
                if (game["AppName"] == manifest.AppName)
                {
                    game["InstallLocation"] = newInstall;
                    break;
                }
            }
            File.WriteAllText(launcherInstalledPath, JsonConvert.SerializeObject(data, Formatting.Indented));

            // Write manifest
            var oldPath = manifest.FilePath;
            File.Copy(oldPath, oldPath + ".bak", true);
            
            var itemData = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(oldPath));
            itemData["InstallLocation"] = manifest.InstallLocation;
            itemData["ManifestLocation"] = manifest.ManifestLocation;
            itemData["StagingLocation"] = manifest.StagingLocation;
            File.WriteAllText(oldPath, JsonConvert.SerializeObject(itemData, Formatting.Indented));
        }

        private bool IsSubdir(string baseDir, string path)
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(baseDir), StringComparison.OrdinalIgnoreCase);
        }
    }
}