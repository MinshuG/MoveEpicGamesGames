using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading.Tasks;
using MoveEpicGamesGames.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MoveEpicGamesGames.Services
{
    public static class GameBackupService
    {
        // TODO: use manifest to determine which files to backup
        public static async Task BackupGameAsync(GameManifest manifest, string zipFilePath, IProgress<(string Operation, double Progress)>? progress = null)
        {
            long totalSize = new DirectoryInfo(manifest.InstallLocation).EnumerateFiles("*", SearchOption.AllDirectories).Sum(fi => fi.Length);
            long currentSize = 0;

            void ReportProgress(string operation, long bytes)
            {
                currentSize += bytes;
                progress?.Report((operation, (double)currentSize / totalSize));
            }

            using var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create);
            progress?.Report(("Preparing backup...", 0));
            // Add game files
            AddDirectoryToZip(zipArchive, manifest.InstallLocation, "GameFiles", ReportProgress);

            // Add manifest files
            // only include if manifest location is not subdirectory of install location
            // which is the case for most games
            if (!manifest.ManifestLocation.StartsWith(manifest.InstallLocation))
                AddDirectoryToZip(zipArchive, manifest.ManifestLocation, "Manifests");

            if (!manifest.StagingLocation.StartsWith(manifest.InstallLocation))
                AddFileToZip(zipArchive, manifest.StagingLocation, "Manifests");

            // Add LauncherInstalled entry
            var launcherInstalledPath = @"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat";
            var launcherData = JsonConvert.DeserializeObject<dynamic>(await File.ReadAllTextAsync(launcherInstalledPath));
                
            var installationList = (IEnumerable<dynamic>)launcherData["InstallationList"];
            var gameEntry = installationList.FirstOrDefault(entry => entry["AppName"] == manifest.AppName);
            if (gameEntry != null)
            {
                var entry = zipArchive.CreateEntry("LauncherInstalled.json");
                await using var entryStream = entry.Open();
                await using var writer = new StreamWriter(entryStream);
                await writer.WriteAsync(JsonConvert.SerializeObject(gameEntry, Formatting.Indented));
            }
                
            // add manifest.FilePath 
            AddFileToZip(zipArchive, manifest.FilePath, new FileInfo(manifest.FilePath).Name);
        }

        public static async Task<GameManifest> GetManifestFromZip(string zipFilePath)
        {
            using var archive = ZipFile.OpenRead(zipFilePath);

            var manifestEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".item"));
            if (manifestEntry == null) throw new Exception("Invalid backup: Missing manifest file");
        
            string manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            string manifestDestPath = Path.Combine(manifestsPath, manifestEntry.Name);
        
            // Backup existing manifest if it exists
            if (File.Exists(manifestDestPath))
            {
                File.Copy(manifestDestPath, manifestDestPath + ".bak", true);
            }
        
            // Extract and update manifest
            manifestEntry.ExtractToFile(manifestDestPath, true);
            var manifest = JsonConvert.DeserializeObject<GameManifest>(await File.ReadAllTextAsync(manifestDestPath));
            return manifest;
        }
        
        public static async Task RestoreBackupAsync(string zipFilePath, string restorePath, IProgress<(string Operation, double Progress)>? progress = null)
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
        
            // Get total size for progress calculation
            var totalSize = archive.Entries.Sum(e => e.Length);
            long currentSize = 0;

            void ReportProgress(string operation, long bytes)
            {
                currentSize += bytes;
                progress?.Report((operation, (double)currentSize / totalSize));
            }

            progress?.Report(("Reading backup info...", 0));
        
            // First read the LauncherInstalled.json to get game info
            var launcherEntry = archive.GetEntry("LauncherInstalled.json");
            if (launcherEntry == null) throw new Exception("Invalid backup: Missing LauncherInstalled.json");
            
            using var reader = new StreamReader(launcherEntry.Open());
            var gameInfo = JsonConvert.DeserializeObject<dynamic>(await reader.ReadToEndAsync());
            string appName = gameInfo["AppName"].ToString();

            string gameInstallPath = Path.Combine(restorePath, Path.GetFileName(gameInfo["InstallLocation"].ToString()));

            #region LauncherInstalled
            // Update LauncherInstalled.dat
            string launcherPath = @"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat";
            File.Copy(launcherPath, launcherPath + ".bak", true);
        
            var launcherData = JObject.Parse(await File.ReadAllTextAsync(launcherPath));
            var installationList = launcherData["InstallationList"] as JArray;

            foreach (var entry in installationList!)
            {
                if (entry["AppName"]?.ToString() == appName)
                {
                    entry["InstallLocation"] = gameInstallPath;
                    throw new Exception("Game already installed. Uninstall it first.");
                }
            }
            #endregion

            progress?.Report(("Extracting game files...", 0.05));

            // Extract game files
            var gameFiles = archive.Entries.Where(e => e.FullName.StartsWith("GameFiles\\"));

            foreach (var entry in gameFiles)
            {
                string relativePath = entry.FullName.Substring("GameFiles/".Length);
                string destinationPath = Path.Combine(gameInstallPath, relativePath);
            
                if (entry.FullName.EndsWith("/"))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }
            
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await ExtractEntryWithProgress(entry, destinationPath, 
                    (op, bytes) => ReportProgress($"Extracting: {Path.GetFileName(destinationPath)}", entry.Length));
            }

            progress?.Report(("Updating manifests...", 0.95));

            # region .item file
            // Extract and update manifest file
            var manifestEntry = archive.Entries.FirstOrDefault(e => e.Name.EndsWith(".item"));
            if (manifestEntry == null) throw new Exception("Invalid backup: Missing manifest file");
        
            string manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            string manifestDestPath = Path.Combine(manifestsPath, manifestEntry.Name);
        
            // Backup existing manifest if it exists
            if (File.Exists(manifestDestPath)) // already installed game?
            {
                File.Copy(manifestDestPath, manifestDestPath + ".bak", true);
            }
        
            // Extract and update manifest
            manifestEntry.ExtractToFile(manifestDestPath, true);
            var manifest = JsonConvert.DeserializeObject<dynamic>(File.ReadAllText(manifestDestPath));
            manifest["InstallLocation"] = gameInstallPath;
            await File.WriteAllTextAsync(manifestDestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
            #endregion

            // add game to LauncherInstalled.dat
            
            installationList!.Add(JObject.FromObject(gameInfo));

            await File.WriteAllTextAsync(launcherPath, launcherData.ToString(Formatting.Indented));

            progress?.Report(("Restore complete", 1.0));
        }

        private static async Task ExtractEntryWithProgress(ZipArchiveEntry entry, string destinationPath, Action<string, long> reportProgress)
        {
            using var entryStream = entry.Open();
            using var fileStream = File.Create(destinationPath);
            await entryStream.CopyToAsync(fileStream);
            reportProgress($"Extracting: {Path.GetFileName(destinationPath)}", entry.Length);
        }

        private static void AddDirectoryToZip(ZipArchive zipArchive, string sourceDir, string entryName, Action<string, long>? progressCallback = null)
        {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, dir);
                zipArchive.CreateEntry(Path.Combine(entryName, relativePath) + "/");
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                AddFileToZip(zipArchive, file, Path.Combine(entryName, relativePath), progressCallback);
            }
        }

        private static void AddFileToZip(ZipArchive zipArchive, string filePath, string entryName, Action<string, long>? progressCallback = null)
        {
            var entry = zipArchive.CreateEntry(entryName);
            using (var entryStream = entry.Open())
            using (var fileStream = File.OpenRead(filePath))
            {
                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryStream.Write(buffer, 0, bytesRead);
                    progressCallback?.Invoke($"Backing up: {Path.GetFileName(filePath)}", bytesRead);
                }
            }
        }
    }
}
