using System.Text.Json.Serialization;

namespace YandexMusicSharp.Models;

public sealed class LyricsInfo
{
    [JsonPropertyName("downloadUrl")]
    public string DownloadUrl { get; init; } = string.Empty;

    [JsonPropertyName("lyricId")]
    public long? LyricId { get; init; }

    [JsonPropertyName("externalLyricId")]
    public string? ExternalLyricId { get; init; }

    [JsonPropertyName("writers")]
    public IReadOnlyList<string>? Writers { get; init; }

    [JsonPropertyName("major")]
    public LyricsMajor? Major { get; init; }
}

public sealed class LyricsMajor
{
    [JsonPropertyName("id")]
    public long? Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("prettyName")]
    public string? PrettyName { get; init; }
}

public enum LyricsFormat
{
    Text,
    Lrc,
}
