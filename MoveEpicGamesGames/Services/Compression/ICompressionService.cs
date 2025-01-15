using System;
using System.IO;

namespace MoveEpicGamesGames.Services.Compression;

public enum ArchiveOpenMode
{
    Compress,
    Decompress
}

public interface IArchive : IDisposable
{
    public string[] Entries { get; }

    Stream? GetEntry(string entryName);
    long GetTotalExtractedSize();
    void ExtractAll(string destinationDir, Action<string, long> progressCallback);
    
    void ExtractToFile(string entryName, string destinationFile);

    void AddEntry(string entryName, Stream? content);
    
    Stream CreateEntry(string entryName, long contentLength);
}

public interface ICompressionService
{
    string FileExtension { get; }
    // Task CompressDirectoryAsync(string sourceDir, string destinationFile, Action<string, long> progressCallback);
    IArchive OpenArchive(string archiveFile, ArchiveOpenMode mode);
}
