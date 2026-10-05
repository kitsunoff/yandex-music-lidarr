using YandexMusicSharp.Api;
using YandexMusicSharp.Auth;
using YandexMusicSharp.Codec;
using YandexMusicSharp.Http;
using YandexMusicSharp.Models;

namespace YandexMusicSharp;

/// <summary>
/// Façade over the Yandex.Music API surface needed by the Lidarr plugin.
/// Exposes the indexer-side endpoints (search, artist discography), the
/// download-side endpoints (album with tracks, get-file-info) and a helper
/// that downloads an encrypted stream and returns the decrypted bytes.
/// </summary>
public sealed class YandexMusicClient : IDisposable
{
    private readonly YandexMusicHttpClient _http;

    public YandexMusicClient(YandexMusicCredentials credentials)
        : this(credentials, new HttpClient())
    {
    }

    public YandexMusicClient(YandexMusicCredentials credentials, HttpClient httpClient)
    {
        _http = new YandexMusicHttpClient(credentials, httpClient);
        Search = new SearchClient(_http);
        Albums = new AlbumsClient(_http);
        Artists = new ArtistsClient(_http);
        Downloads = new DownloadInfoClient(_http);
        Lyrics = new LyricsClient(_http);
    }

    public SearchClient Search { get; }

    public AlbumsClient Albums { get; }

    public ArtistsClient Artists { get; }

    public DownloadInfoClient Downloads { get; }

    public LyricsClient Lyrics { get; }

    /// <summary>
    /// Fetches the first available CDN URL listed in <paramref name="info"/>,
    /// decrypts the bytes with the AES-CTR key carried in
    /// <see cref="DownloadInfo.Key"/>, and writes the plain audio file into
    /// <paramref name="destination"/>.  Decryption is streamed chunk-by-chunk
    /// so a multi-hundred-megabyte hi-res FLAC does not need to be buffered
    /// in memory.  Reports cumulative byte counts via the optional
    /// <paramref name="progress"/> callback (useful for ETA reporting).
    /// Falls back to the next URL on transport errors.
    /// </summary>
    public async Task DownloadDecryptedToAsync(
        DownloadInfo info,
        Stream destination,
        IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(destination);
        if (info.Urls.Count == 0)
        {
            throw new YandexMusicException("DownloadInfo carries no CDN URLs.");
        }
        if (string.IsNullOrEmpty(info.Key))
        {
            throw new YandexMusicException("DownloadInfo is missing an AES decryption key - was the encraw transport requested?");
        }

        var startPosition = destination.CanSeek ? destination.Position : 0L;
        Exception? lastError = null;

        for (var urlIndex = 0; urlIndex < info.Urls.Count; urlIndex++)
        {
            var url = info.Urls[urlIndex];
            try
            {
                using var decryptor = new AesCtrStream(Convert.FromHexString(info.Key));
                using var response = await _http.GetStreamingAsync(new Uri(url, UriKind.Absolute), cancellationToken).ConfigureAwait(false);
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

                var buffer = new byte[64 * 1024];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    decryptor.Transform(buffer.AsSpan(0, read));
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    total += read;
                    progress?.Report(total);
                }
                return;
            }
            catch (Exception ex) when (IsCdnTransportFailure(ex, cancellationToken))
            {
                lastError = ex;

                if (urlIndex == info.Urls.Count - 1)
                {
                    break;
                }

                // A failed CDN may have already written a partial decrypted payload.
                // Rewinding without truncating would append the next CDN attempt to that
                // partial file and silently corrupt the track.  FileStream/MemoryStream,
                // which are the supported destinations in this library, can be rolled
                // back safely to the position they had before the first attempt.
                if (!destination.CanSeek)
                {
                    throw new YandexMusicException(
                        "A CDN download failed after writing to a non-seekable destination; safe fallback is impossible without risking a corrupted track.",
                        ex);
                }

                destination.SetLength(startPosition);
                destination.Position = startPosition;
            }
        }

        throw new YandexMusicException("All CDN URLs failed to deliver the encrypted track.", lastError!);
    }


    private static bool IsCdnTransportFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            // Preserve an explicit caller cancellation.  HttpClient also uses
            // TaskCanceledException/OperationCanceledException for request timeouts;
            // those are transport failures and may safely fall back to another CDN.
            return !cancellationToken.IsCancellationRequested;
        }

        return exception is HttpRequestException or YandexMusicException or IOException;
    }

    /// <summary>
    /// Backwards-compatible variant of <see cref="DownloadDecryptedToAsync"/>
    /// that buffers the whole decrypted payload into a byte array.
    /// </summary>
    public async Task<byte[]> DownloadDecryptedAsync(DownloadInfo info, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await DownloadDecryptedToAsync(info, buffer, progress: null, cancellationToken).ConfigureAwait(false);
        return buffer.ToArray();
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
