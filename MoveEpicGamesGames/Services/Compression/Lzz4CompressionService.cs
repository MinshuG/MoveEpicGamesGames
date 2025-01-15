using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using GenericReader;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace MoveEpicGamesGames.Services.Compression;

public class Lz4DecodeStream: Stream
{
    private LZ4DecoderStream _internalStream;

    public Lz4DecodeStream(LZ4DecoderStream stream, long length)
    {
        _internalStream = stream;
        Length = length;
    }
    
    public override void Flush()
    {
        _internalStream.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        return _internalStream.Read(buffer, offset, count);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        return _internalStream.Seek(offset, origin);
    }

    public override void SetLength(long value)
    {
        _internalStream.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        _internalStream.Write(buffer, offset, count);
    }

    public override bool CanRead => _internalStream.CanRead;
    public override bool CanSeek => _internalStream.CanSeek;
    public override bool CanWrite => _internalStream.CanWrite;
    public override long Length { get; }
    public override long Position
    {
        get => _internalStream.Position;
        set => _internalStream.Position = value;
    }
}

public class IndexEntry
{
    public string RelativePath { get; set; }
    public long ContentLength { get; set; }
    public long Offset { get; set; } 
    
    public IndexEntry Read(GenericStreamReader reader)
    {
        RelativePath = reader.ReadString(Encoding.Default);
        ContentLength = reader.Read<long>();
        Offset = reader.Read<long>();
        return this;
    }

    public void Write(BinaryWriter writer)
    {
        writer.Write(RelativePath.Length);
        writer.Write(Encoding.UTF8.GetBytes(RelativePath));
        writer.Write(ContentLength);
        writer.Write(Offset);
    }
}

struct Lz4Header
{
    public const int Magic = 0x657069; // 69 70 65 in little endian
    public const int SIZE = 20;

    public int MagicNumber { get; set; }
    public int Version { get; set; }
    public int FileCount { get; set; }
    public long IndexOffset { get; set; }
}

public class Lz4Archive : IArchive
{
    public readonly Stream InternalArchiveFile;
    private Lz4Header Header;

    private List<IndexEntry> _entries = [];
    
    public string[] Entries => _entries.Select(e => e.RelativePath).ToArray();

    private readonly ArchiveOpenMode Mode;
    private LZ4Level CompressionLevel = LZ4Level.L00_FAST;

    public Lz4Archive(string archiveFile, ArchiveOpenMode mode)
    {
        Mode = mode;
        InternalArchiveFile = mode switch
        {
            ArchiveOpenMode.Compress => File.Create(archiveFile),
            ArchiveOpenMode.Decompress => File.Open(archiveFile, FileMode.Open, FileAccess.Read),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        if (mode == ArchiveOpenMode.Decompress)
        {
            var reader = new GenericStreamReader(InternalArchiveFile);
            var headerOffset = reader.Length - Lz4Header.SIZE; // at end  
            reader.Position = headerOffset;

            var header = new Lz4Header
            {
                MagicNumber = reader.Read<int>(),
                Version = reader.Read<int>(),
                FileCount = reader.Read<int>(),
                IndexOffset = reader.Read<long>(),
            };

            if (header.MagicNumber != Lz4Header.Magic)
                throw new InvalidDataException("Invalid LZ4 archive header");

            Header = header;
            ReadIndex(reader);
            
            InternalArchiveFile = File.Open(archiveFile, FileMode.Open, FileAccess.Read); // GeneralStreamReader closes the stream 
        }

        if (mode == ArchiveOpenMode.Compress)
        {
            Header = new Lz4Header {Version = 1, FileCount = 0, MagicNumber = Lz4Header.Magic};
            InternalArchiveFile.Position = Unsafe.SizeOf<Lz4Header>();
        }
    }

    private void ReadIndex(GenericStreamReader reader)
    {
        reader.PositionLong = (int)Header.IndexOffset;
        _entries = reader.ReadArray( () => new IndexEntry().Read(reader)).ToList();
    }

    private void WriteIndex(BinaryWriter writer)
    {
        Header.IndexOffset = InternalArchiveFile.Position;
        writer.Write(_entries.Count);
        foreach (var entry in _entries)
        {
            entry.Write(writer);
        }
    }

    public Stream? GetEntry(string entry)
    {
        if (_entries == null)
            throw new InvalidOperationException("Archive is not in decompression mode");

        var foundEntry = _entries.FirstOrDefault(e => e.RelativePath == entry);
        if (foundEntry is null)
            throw new FileNotFoundException($"Entry {entry} not found in archive");
        
        if (foundEntry.ContentLength == -1)
            return null;

        InternalArchiveFile.Position = foundEntry.Offset;
        var decompressStream = new Lz4DecodeStream(LZ4Stream.Decode(InternalArchiveFile, leaveOpen: true), foundEntry.ContentLength);
        return decompressStream;
    }

    public long GetTotalExtractedSize()
    {
        return _entries.Sum(entry => entry.ContentLength);
    }

    public void ExtractAll(string destinationDir, Action<string, long> progressCallback)
    {
        if (Mode == ArchiveOpenMode.Compress)
            throw new InvalidOperationException("Archive is not in decompression mode");

        lock (InternalArchiveFile)
        {
            foreach (var entry in _entries)
            {
                var relativePath = entry.RelativePath;
                var contentLength = entry.ContentLength;
                var offset = entry.Offset;

                var fullPath = Path.Combine(destinationDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                
                if (contentLength == -1)
                    continue;

                InternalArchiveFile.Position = offset;
                using var decompressStream = LZ4Stream.Decode(InternalArchiveFile, leaveOpen: true);
                using var fileStream = File.Create(fullPath);

                var buffer = new byte[81920];
                int bytesRead;
                while ((bytesRead =  decompressStream.Read(buffer)) > 0) // contentLength?
                {
                    fileStream.Write(buffer, 0, bytesRead);
                    progressCallback?.Invoke($"Extracting: {relativePath}", bytesRead);
                }
            }
        }
    }

    public void ExtractToFile(string entryName, string destinationFile)
    {
        if (Mode == ArchiveOpenMode.Compress)
            throw new InvalidOperationException("Archive is not in decompression mode");

        var entry = _entries.FirstOrDefault(e => e.RelativePath == entryName);
        if (entry is null)
            throw new FileNotFoundException($"Entry {entryName} not found in archive");

        if (entry.ContentLength == -1)
        {
            Directory.CreateDirectory(destinationFile);
            return;
        }

        InternalArchiveFile.Position = entry.Offset;
        using var decompressStream = LZ4Stream.Decode(InternalArchiveFile, leaveOpen: true);
        using var fileStream = File.Create(destinationFile);
        decompressStream.CopyTo(fileStream);
    }

    public void AddEntry(string entryName, Stream? content)
    {
        if (Mode == ArchiveOpenMode.Decompress)
            throw new InvalidOperationException("Archive is not in compression mode");

        var entry = new IndexEntry
        {
            RelativePath = entryName,
            ContentLength = content?.Length ?? -1,
            Offset = InternalArchiveFile.Position
        };

        if (content is not null) // file
        {
            using var compressStream = LZ4Stream.Encode(InternalArchiveFile, CompressionLevel, leaveOpen: true); 

            content.CopyTo(compressStream);
        }

        _entries.Add(entry);
    }

    public Stream CreateEntry(string entryName, long contentLength)
    {
        if (Mode == ArchiveOpenMode.Decompress)
            throw new InvalidOperationException("Archive is not in compression mode");
        
        var entry = new IndexEntry
        {
            RelativePath = entryName,
            ContentLength = contentLength,
            Offset = InternalArchiveFile.Position
        };
        _entries.Add(entry);
        return LZ4Stream.Encode(InternalArchiveFile, CompressionLevel, leaveOpen: true);
    }

    public void Dispose()
    {
        if (Mode == ArchiveOpenMode.Compress)
        {
              var writer = new BinaryWriter(InternalArchiveFile);
              WriteIndex(writer);

              writer.Write(Header.MagicNumber);
              writer.Write(Header.Version);
              Header.FileCount = _entries.Count;
              writer.Write(Header.FileCount);
              writer.Write(Header.IndexOffset);

              writer.BaseStream.Position = InternalArchiveFile.Length;
              InternalArchiveFile.Flush();
        }
        
        InternalArchiveFile.Dispose();
    }
}

public class Lz4CompressionService : ICompressionService
{
    public string FileExtension => ".epiclz4";

    public IArchive OpenArchive(string archiveFile, ArchiveOpenMode mode) => new Lz4Archive(archiveFile, mode);
}