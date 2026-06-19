using System.Text.Json.Serialization;

namespace YmbThatuation.Models;

public class ExtensionInfo
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("manifest_version")]
    public ulong ManifestVersion { get; set; }
}
