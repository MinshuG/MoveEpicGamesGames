using System;
using MoveEpicGamesGames.Models;
using MoveEpicGamesGames.Services.Compression;

namespace MoveEpicGamesGames.Services;

public static class CompressionFactory
{
    public static ICompressionService GetService(CompressionMethod method) => method switch
    {
        CompressionMethod.Zip => new ZipCompressionService(),
        CompressionMethod.Lz4 => new Lz4CompressionService(),
        _ => throw new NotImplementedException()
    };
}
