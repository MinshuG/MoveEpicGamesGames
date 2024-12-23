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
        public static async Task BackupGameAsync(GameManifest manifest, string zipFilePath)
        {
            using (var zipArchive = ZipFile.Open(zipFilePath, ZipArchiveMode.Create))
            {
                // Add game files
                AddDirectoryToZip(zipArchive, manifest.InstallLocation, "GameFiles");

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
            var manifest = JsonConvert.DeserializeObject<GameManifest>(File.ReadAllText(manifestDestPath));
            return manifest;
        }
        
        public static async Task RestoreBackupAsync(string zipFilePath, string restorePath)
        {
            using var archive = ZipFile.OpenRead(zipFilePath);
        
            // First read the LauncherInstalled.json to get game info
            var launcherEntry = archive.GetEntry("LauncherInstalled.json");
            if (launcherEntry == null) throw new Exception("Invalid backup: Missing LauncherInstalled.json");
        
            using var reader = new StreamReader(launcherEntry.Open());
            var gameInfo = JsonConvert.DeserializeObject<dynamic>(await reader.ReadToEndAsync());
            string appName = gameInfo["AppName"].ToString();
        
            // Extract game files
            var gameFiles = archive.Entries.Where(e => e.FullName.StartsWith("GameFiles\\"));
            string gameInstallPath = Path.Combine(restorePath, Path.GetFileName(gameInfo["InstallLocation"].ToString()));
        
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
                entry.ExtractToFile(destinationPath, true);
            }
            
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
            File.WriteAllText(manifestDestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
            #endregion

            // Update LauncherInstalled.dat
            string launcherPath = @"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat";
            File.Copy(launcherPath, launcherPath + ".bak", true);
        
            var launcherData = JObject.Parse(await File.ReadAllTextAsync(launcherPath));
            var installationList = launcherData["InstallationList"] as JArray;
            
            bool found = false;
            foreach (var entry in installationList!)
            {
                if (entry["AppName"]?.ToString() == appName)
                {
                    entry["InstallLocation"] = gameInstallPath;
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                installationList!.Add(JObject.FromObject(gameInfo));
            }

            await File.WriteAllTextAsync(launcherPath, launcherData.ToString(Formatting.Indented));
        }

        private static void AddDirectoryToZip(ZipArchive zipArchive, string sourceDir, string entryName)
        {
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, dir);
                zipArchive.CreateEntry(Path.Combine(entryName, relativePath) + "/");
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                AddFileToZip(zipArchive, file, Path.Combine(entryName, relativePath));
            }
        }

        private static void AddFileToZip(ZipArchive zipArchive, string filePath, string entryName)
        {
            var entry = zipArchive.CreateEntry(entryName);
            using (var entryStream = entry.Open())
            using (var fileStream = File.OpenRead(filePath))
            {
                fileStream.CopyTo(entryStream);
            }
        }
    }
}
