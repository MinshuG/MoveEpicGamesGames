using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;
using System.Linq;

namespace MoveEpicGamesGames.Services.Compression;

public class ZipArchive : IArchive
{
    private readonly System.IO.Compression.ZipArchive _archive;
    
    public string[] Entries => _archive.Entries.Select(e => e.FullName).ToArray();

    public ZipArchive(string archiveFile, ArchiveOpenMode mode)
    {
        _archive = mode switch
        {
            ArchiveOpenMode.Compress => ZipFile.Open(archiveFile, ZipArchiveMode.Create),
            ArchiveOpenMode.Decompress => ZipFile.Open(archiveFile, ZipArchiveMode.Read),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    public Stream GetEntry(string entryName)
    {
        var entry = _archive.GetEntry(entryName);
        if (entry == null)
            throw new FileNotFoundException($"Entry {entryName} not found in archive");

        return entry.Open();
    }

    public long GetTotalExtractedSize() => _archive.Entries.Sum(entry => entry.Length);

    public void ExtractAll(string destinationDir, Action<string, long> progressCallback)
    {
        foreach (var entry in _archive.Entries)
        {
            var destinationPath = Path.Combine(destinationDir, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            
            using var entryStream = entry.Open();
            using var fileStream = File.Create(destinationPath);
            
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = entryStream.Read(buffer)) > 0)
            {
                 fileStream.Write(buffer, 0, bytesRead);
                progressCallback?.Invoke($"Extracting: {entry.Name}", bytesRead);
            }
        }
    }

    public void ExtractToFile(string entryName, string destinationFile)
    {
        var entry = _archive.GetEntry(entryName);
        if (entry == null)
            throw new FileNotFoundException($"Entry {entryName} not found in archive");

        entry.ExtractToFile(destinationFile);
    }

    public void AddEntry(string entryName, Stream? content)
    {
        var entry = _archive.CreateEntry(entryName);
        if (content == null)
            return;
        using var entryStream = entry.Open();
        content.CopyTo(entryStream);
    }

    public Stream CreateEntry(string entryName, long contentLength)
    {
        var entry = _archive.CreateEntry(entryName);
        return entry.Open();
    }

    public void Dispose() => _archive.Dispose();
}

public class ZipCompressionService : ICompressionService
{
    public string FileExtension => ".epiczip";

    public async Task CompressDirectoryAsync(string sourceDir, string destinationFile, Action<string, long> progressCallback)
    {
        using var archive = ZipFile.Open(destinationFile, ZipArchiveMode.Create);
        
        foreach (var file in Directory.GetFiles(sourceDir, "*.*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var entry = archive.CreateEntry(relativePath);
            
            using var entryStream = entry.Open();
            using var fileStream = File.OpenRead(file);
            
            var buffer = new byte[81920];
            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer)) > 0)
            {
                await entryStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                progressCallback?.Invoke($"Compressing: {relativePath}", bytesRead);
            }
        }
    }

    public IArchive OpenArchive(string archiveFile, ArchiveOpenMode mode) => new ZipArchive(archiveFile, mode);
}
