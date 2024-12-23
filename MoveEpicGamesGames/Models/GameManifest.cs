using System.Collections.Generic;
using Newtonsoft.Json;

namespace MoveEpicGamesGames.Models;

public class GameManifest // not full class, only relevant properties
{
    [JsonProperty("DisplayName")]
    public string DisplayName { get; set; } = string.Empty;
    
    [JsonProperty("AppName")]
    public string AppName { get; set; } = string.Empty;
    
    [JsonProperty("InstallLocation")]
    public string InstallLocation { get; set; } = string.Empty;
    
    [JsonProperty("ManifestLocation")]
    public string ManifestLocation { get; set; } = string.Empty;
    
    [JsonProperty("StagingLocation")]
    public string StagingLocation { get; set; } = string.Empty;
    
    [JsonProperty("ExpectingDLCInstalled")]
    public Dictionary<string, object> ExpectingDLCInstalled { get; set; } = new();

    [JsonIgnore]
    public string FilePath { get; set; } = string.Empty;
}
