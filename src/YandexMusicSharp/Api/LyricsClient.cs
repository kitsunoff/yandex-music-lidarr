using System.Globalization;
using System.Text;
using YandexMusicSharp.Auth;
using YandexMusicSharp.Http;
using YandexMusicSharp.Models;

namespace YandexMusicSharp.Api;

/// <summary>
/// Fetches lyrics metadata from the signed <c>/tracks/{id}/lyrics</c> endpoint
/// and then downloads the actual TEXT/LRC payload from the returned presigned URL.
/// </summary>
public sealed class LyricsClient
{
    private readonly YandexMusicHttpClient _http;
    private readonly Func<long> _timestampSource;

    public LyricsClient(YandexMusicHttpClient http)
        : this(http, () => DateTimeOffset.UtcNow.ToUnixTimeSeconds())
    {
    }

    internal LyricsClient(YandexMusicHttpClient http, Func<long> timestampSource)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(timestampSource);
        _http = http;
        _timestampSource = timestampSource;
    }

    public async Task<string> GetAsync(
        long trackId,
        long durationMs,
        LyricsFormat format,
        CancellationToken cancellationToken = default)
    {
        var timestamp = _timestampSource();
        var signature = RequestSigner.SignLyrics(trackId, timestamp);
        var formatValue = format == LyricsFormat.Lrc ? "LRC" : "TEXT";

        var parameters = new Dictionary<string, string?>
        {
            ["format"] = formatValue,
            ["durationMs"] = durationMs > 0 ? durationMs.ToString(CultureInfo.InvariantCulture) : null,
            ["timeStamp"] = timestamp.ToString(CultureInfo.InvariantCulture),
            ["sign"] = signature,
        };

        var info = await _http.GetAsync<LyricsInfo>(
            $"/tracks/{trackId.ToString(CultureInfo.InvariantCulture)}/lyrics",
            parameters,
            cancellationToken).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(info.DownloadUrl))
        {
            throw new YandexMusicException("Lyrics response did not include a downloadUrl.");
        }

        var bytes = await _http.GetBytesAsync(new Uri(info.DownloadUrl, UriKind.Absolute), cancellationToken).ConfigureAwait(false);
        var lyrics = Encoding.UTF8.GetString(bytes);

        // Some providers prepend an UTF-8 BOM to the text file.  It has no semantic
        // meaning in the tag and may otherwise show up as an invisible first character.
        if (lyrics.Length > 0 && lyrics[0] == '\uFEFF')
        {
            lyrics = lyrics[1..];
        }

        if (string.IsNullOrWhiteSpace(lyrics))
        {
            throw new YandexMusicException("Lyrics download returned an empty document.");
        }

        return lyrics;
    }
}
