using System.Text.Json.Serialization;

namespace YmbThatuation.Models;

public class UiInstance
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("color")]
    public string Color { get; set; } = "";

    [JsonPropertyName("letter")]
    public string Letter { get; set; } = "";

    [JsonPropertyName("recipe")]
    public string Recipe { get; set; } = "";

    [JsonPropertyName("custom_icon")]
    public string? CustomIcon { get; set; }

    [JsonPropertyName("alive")]
    public bool Alive { get; set; }

    [JsonPropertyName("active")]
    public bool Active { get; set; }

    [JsonPropertyName("unread")]
    public uint Unread { get; set; }
}
