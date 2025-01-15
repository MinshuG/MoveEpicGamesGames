using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MoveEpicGamesGames.Models;
using MoveEpicGamesGames.Services.Compression;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using ZipArchive = MoveEpicGamesGames.Services.Compression.ZipArchive;

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

            IArchive archive;
            if (zipFilePath.EndsWith(".epiczip"))
            {
                archive = new ZipArchive(zipFilePath, ArchiveOpenMode.Compress);
            }
            else if (zipFilePath.EndsWith(".epiclz4"))
            {
                archive = new Lz4Archive(zipFilePath, ArchiveOpenMode.Compress);
            }
            else
            {
                throw new Exception("Invalid backup file format");
            }
            
            
            progress?.Report(("Preparing backup...", 0));
            // Add game files
            AddDirectoryToAr(archive, manifest.InstallLocation, "GameFiles", ReportProgress);

            // Add manifest files
            // only include if manifest location is not subdirectory of install location
            // which is the case for most games
            if (!manifest.ManifestLocation.Replace("/", "\\").StartsWith(manifest.InstallLocation))
                AddDirectoryToAr(archive, manifest.ManifestLocation, "Manifests");

            if (!manifest.StagingLocation.Replace("/", "\\").StartsWith(manifest.InstallLocation))
                AddDirectoryToAr(archive, manifest.StagingLocation, "Manifests");

            // Add LauncherInstalled entry
            var launcherInstalledPath = @"C:\ProgramData\Epic\UnrealEngineLauncher\LauncherInstalled.dat";
            var launcherData = JsonConvert.DeserializeObject<dynamic>(await File.ReadAllTextAsync(launcherInstalledPath));
                
            var installationList = (IEnumerable<dynamic>)launcherData["InstallationList"];
            var gameEntry = installationList.FirstOrDefault(entry => entry["AppName"] == manifest.AppName);
            if (gameEntry != null)
            {
                var stream = new MemoryStream();
                stream.Write(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(gameEntry, Formatting.Indented)));
                stream.Position = 0;
                archive.AddEntry("LauncherInstalled.json", stream);
            }

            // add manifest.FilePath 
            AddFileToAr(archive, manifest.FilePath, new FileInfo(manifest.FilePath).Name);
            archive.Dispose();
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
            if (!Directory.Exists(restorePath)) Directory.CreateDirectory(restorePath);

            ICompressionService compressionService;
            if (zipFilePath.EndsWith(".epiczip"))
            {
                compressionService = new ZipCompressionService();
            }
            else if (zipFilePath.EndsWith(".epiclz4"))
            {
                compressionService = new Lz4CompressionService();
            }
            else
            {
                throw new Exception("Invalid backup file format");
            }
            
            var archive = compressionService.OpenArchive(zipFilePath, ArchiveOpenMode.Decompress);
            
            // Get total size for progress calculation
            var totalSize = archive.GetTotalExtractedSize();
            long currentSize = 0;

            void ReportProgress(string operation, long bytes)
            {
                currentSize += bytes;
                progress?.Report((operation, ((double)currentSize / totalSize) * 0.95f));
            }

            progress?.Report(("Reading backup info...", 0));
        
            // First read the LauncherInstalled.json to get game info
            var launcherEntry = archive.GetEntry("LauncherInstalled.json");
            if (launcherEntry == null) throw new Exception("Invalid backup: Missing LauncherInstalled.json");

            var temp = new MemoryStream();
            await launcherEntry.CopyToAsync(temp);
            temp.Position = 0;
            using var reader = new StreamReader(temp);
            var gameInfo = JsonConvert.DeserializeObject<dynamic>(await reader.ReadToEndAsync());
            string appName = gameInfo["AppName"].ToString();

            string gameInstallPath = Path.Combine(restorePath, Path.GetFileName(gameInfo["InstallLocation"].ToString()));
            
            gameInfo["InstallLocation"] = gameInstallPath; // update install location to new path

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
                    // entry["InstallLocation"] = gameInstallPath; // change install location??
                    throw new Exception("Game already installed. Uninstall it first.");
                }
            }
            
            #endregion

            progress?.Report(("Extracting game files...", 0.05));

            // Extract game files
            var gameFiles = archive.Entries.Where(e => e.StartsWith("GameFiles\\"));

            foreach (var entry in gameFiles)
            {
                string relativePath = entry.Substring("GameFiles/".Length);
                string destinationPath = Path.Combine(gameInstallPath, relativePath);
            
                if (entry.EndsWith("/"))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }
            
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                await ExtractEntryWithProgress(archive, entry, destinationPath, 
                    (op, bytes) => ReportProgress($"Extracting: {op}", bytes));
            }

            progress?.Report(("Updating manifests...", 0.95));

            # region .item file
            // Extract and update manifest file
            var manifestEntry = archive.Entries.FirstOrDefault(e => e.EndsWith(".item"));
            if (manifestEntry == null) throw new Exception("Invalid backup: Missing manifest file");
        
            string manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
            string manifestDestPath = Path.Combine(manifestsPath, manifestEntry);
        
            // Backup existing manifest if it exists
            if (File.Exists(manifestDestPath)) // already installed game?
            {
                File.Move(manifestDestPath, manifestDestPath + ".bak", true);
            }
        
            // Extract and update manifest
            archive.ExtractToFile(manifestEntry, manifestDestPath);
            var manifest = JsonConvert.DeserializeObject<dynamic>(await File.ReadAllTextAsync(manifestDestPath));
            manifest!["InstallLocation"] = gameInstallPath;
            await File.WriteAllTextAsync(manifestDestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));
            #endregion

            // add game to LauncherInstalled.dat
            
            installationList!.Add(JObject.FromObject(gameInfo));

            await File.WriteAllTextAsync(launcherPath, launcherData.ToString(Formatting.Indented));

            progress?.Report(("Restore complete", 1.0));
        }

        private static async Task ExtractEntryWithProgress(IArchive archive, string entry, string destinationPath, Action<string, long> reportProgress)
        {
            // TODO: chunked extraction for better progress reporting
            using var entryStream = archive.GetEntry(entry);
            using var fileStream = File.Create(destinationPath);
            await entryStream!.CopyToAsync(fileStream);
            reportProgress($"Extracting: {Path.GetFileName(destinationPath)}", fileStream.Length);
        }

        private static void AddDirectoryToAr(IArchive archive, string sourceDir, string entryPrefix, Action<string, long>? progressCallback = null)
        {
            if (!Directory.Exists(sourceDir)) return;
            foreach (var dir in Directory.GetDirectories(sourceDir, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, dir);
                
                // if the directory is empty, add an empty entry
                if (!Directory.EnumerateFileSystemEntries(dir).Any())
                {
                    archive.AddEntry(Path.Combine(entryPrefix, relativePath) + "/", null);
                }
            }

            foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDir, file);
                AddFileToAr(archive, file, Path.Combine(entryPrefix, relativePath), progressCallback);
            }
        }

        private static void AddFileToAr(IArchive archive, string filePath, string entryName, Action<string, long>? progressCallback = null)
        {
            using (var fileStream = File.OpenRead(filePath))
            {
                var entryStream = archive.CreateEntry(entryName, fileStream.Length);

                var buffer = new byte[65536];
                int bytesRead;
                while ((bytesRead = fileStream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    entryStream.Write(buffer, 0, bytesRead);
                    progressCallback?.Invoke($"Backing up: {Path.GetFileName(filePath)}", bytesRead);
                }
                entryStream.Close();
            }
        }
    }
}
