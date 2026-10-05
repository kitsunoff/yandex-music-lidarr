using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using NLog;
using NzbDrone.Common.Disk;
using NzbDrone.Core.Plugins.YandexMusic;
using YandexMusicSharp;
using YandexMusicSharp.Models;

namespace NzbDrone.Core.Download.Clients.YandexMusic.Queue
{
    /// <summary>
    /// In-process background processor for queued album downloads.  Uses a
    /// bounded <see cref="Channel{T}"/> for backpressure and a single consumer
    /// loop with a per-album SemaphoreSlim for track parallelism.
    /// </summary>
    public sealed class DownloadTaskQueue : IDisposable
    {
        private readonly Channel<DownloadItem> _channel;
        private readonly ConcurrentDictionary<string, DownloadItem> _items = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Logger _logger;
        private YandexMusicSettings? _settings;
        private Task? _consumerLoop;

        public DownloadTaskQueue(int capacity, Logger logger)
        {
            _channel = Channel.CreateBounded<DownloadItem>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
            _logger = logger;
        }

        public void StartQueueHandler()
        {
            _consumerLoop ??= Task.Run(() => RunConsumerLoop(_cts.Token));
        }

        public void SetSettings(YandexMusicSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        public async ValueTask EnqueueAsync(DownloadItem item, CancellationToken cancellationToken = default)
        {
            _items[item.Id] = item;
            await _channel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
        }

        public IEnumerable<DownloadItem> Snapshot() => _items.Values.ToArray();

        public bool TryGet(string id, out DownloadItem? item) => _items.TryGetValue(id, out item);

        public void Remove(string id)
        {
            _items.TryRemove(id, out _);
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            _cts.Cancel();
            _cts.Dispose();
        }

        private async Task RunConsumerLoop(CancellationToken cancellationToken)
        {
            try
            {
                while (await _channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_channel.Reader.TryRead(out var item))
                    {
                        await ProcessAsync(item, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Yandex.Music download queue stopped unexpectedly.");
            }
        }

        private async Task ProcessAsync(DownloadItem item, CancellationToken cancellationToken)
        {
            if (_settings is null)
            {
                MarkFailed(item, "Download client settings were not initialised.");
                return;
            }

            try
            {
                item.Status = DownloadItemStatus.Downloading;
                item.StartedAt = DateTime.UtcNow;

                var client = YandexMusicApi.GetClient(_settings.OAuthToken);
                var album = await client.Albums.GetWithTracksAsync(item.YandexAlbumId, cancellationToken).ConfigureAwait(false);
                if (album.Volumes is null || album.Volumes.Count == 0)
                {
                    MarkFailed(item, "Album has no tracks - it may be unavailable in the current region.");
                    return;
                }

                var albumFolder = BuildAlbumFolder(_settings.DownloadPath, album);
                Directory.CreateDirectory(albumFolder);
                item.OutputPath = new OsPath(albumFolder);

                var tracks = album.Volumes.SelectMany(volume => volume).Where(t => t.Available).ToList();
                if (tracks.Count == 0)
                {
                    MarkFailed(item, "No tracks are available for this album with the bound account.");
                    return;
                }

                CoverArtData? coverArt = null;
                var coverUri = !string.IsNullOrWhiteSpace(album.CoverUri)
                    ? album.CoverUri
                    : tracks.Select(t => t.CoverUri).FirstOrDefault(uri => !string.IsNullOrWhiteSpace(uri));
                if (!string.IsNullOrWhiteSpace(coverUri))
                {
                    try
                    {
                        coverArt = await CoverArtUtilities
                            .DownloadAsync(coverUri, (YandexMusicCoverResolutionOption)_settings.CoverResolution, cancellationToken)
                            .ConfigureAwait(false);

                        if (coverArt is not null)
                        {
                            _logger?.Info(
                                "Yandex.Music album {0}: downloaded cover art ({1}, {2} bytes).",
                                album.Id,
                                coverArt.MimeType,
                                coverArt.Bytes.Length);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Warn(ex, "Failed to download cover art for Yandex.Music album {0}; tracks will be downloaded without embedded artwork.", album.Id);
                    }
                }
                else
                {
                    _logger?.Warn("Yandex.Music album {0} does not contain a coverUri; tracks will be downloaded without embedded artwork.", album.Id);
                }

                // Refine TotalSize from actual track durations + the nominal bitrate of
                // the requested quality.  The indexer-side estimate uses an
                // 3.5 min average track length; the precise total comes from the
                // /albums/{id}/with-tracks payload that we just fetched.
                item.TotalSize = EstimateTotalSize(tracks, item.Quality);

                using var concurrency = new SemaphoreSlim(_settings.MaxConcurrentTracks);
                var downloadTasks = new List<Task>(tracks.Count);

                foreach (var track in tracks)
                {
                    await concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                    downloadTasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await DownloadTrackAsync(client, track, album, albumFolder, item, coverArt, cancellationToken).ConfigureAwait(false);
                        }
                        finally
                        {
                            concurrency.Release();
                        }
                    }, cancellationToken));
                }

                await Task.WhenAll(downloadTasks).ConfigureAwait(false);

                if (item.Status != DownloadItemStatus.Failed)
                {
                    item.Status = DownloadItemStatus.Completed;
                    item.Message = "All tracks downloaded.";
                }
            }
            catch (OperationCanceledException)
            {
                item.Status = DownloadItemStatus.Failed;
                item.Message = "Cancelled.";
            }
            catch (Exception ex)
            {
                _logger?.Error(ex, "Failed to process Yandex.Music download {0}.", item.Id);
                MarkFailed(item, ex.Message);
            }
        }

        private async Task DownloadTrackAsync(
            YandexMusicClient client,
            Track track,
            Album album,
            string albumFolder,
            DownloadItem item,
            CoverArtData? coverArt,
            CancellationToken cancellationToken)
        {
            if (_settings is null)
            {
                return;
            }
            if (!long.TryParse(track.Id, out var trackId))
            {
                throw new FormatException($"Track id '{track.Id}' is not numeric.");
            }

            var info = await client.Downloads.GetAsync(trackId, item.Quality, cancellationToken).ConfigureAwait(false);

            // Make the requested-vs-served codec discrepancy visible.  Yandex silently
            // downgrades to AAC when the account does not have an active Plus
            // subscription or when the specific track is missing a lossless master.
            var requestedQuality = item.Quality.ToApiString();
            var servedQuality = info.Quality;
            var servedCodec = string.IsNullOrEmpty(info.Codec) ? "unknown" : info.Codec;
            if (!string.Equals(requestedQuality, servedQuality, StringComparison.OrdinalIgnoreCase))
            {
                _logger?.Warn(
                    "Yandex.Music track {0} ({1}): requested {2} but server returned {3} ({4}, {5} kbps). Check Yandex Plus on the bound account.",
                    track.Id,
                    track.Title,
                    requestedQuality,
                    servedQuality,
                    servedCodec,
                    info.Bitrate);
            }
            else
            {
                _logger?.Info(
                    "Yandex.Music track {0} ({1}): {2} {3} kbps, size {4} bytes",
                    track.Id,
                    track.Title,
                    servedCodec,
                    info.Bitrate,
                    info.Size?.ToString(CultureInfo.InvariantCulture) ?? "unknown");
            }

            var fileName = TrackFileNamer.BuildFileName(track, album, info);
            var fullPath = Path.Combine(albumFolder, fileName);
            var temporaryPath = BuildTemporaryTrackPath(albumFolder);

            try
            {
                // Stream into a same-directory temporary file first.  The final path is
                // never exposed to Lidarr until the encrypted body has been downloaded,
                // decrypted and tagged successfully enough to keep the track.  The
                // temporary path ends in .tmp so even an orphan from a hard crash is not
                // an importable audio file; TagLib# receives the real media type explicitly.
                // Per-chunk progress reports drive the queue's ETA estimate.
                var lastReported = 0L;
                var progress = new Progress<long>(bytesSoFar =>
                {
                    // A CDN fallback restarts its cumulative byte counter from zero.
                    // Do not subtract the bytes from the failed attempt; once the new
                    // attempt passes that high-water mark, only genuinely new progress
                    // is added and the final counter still converges on file size.
                    var delta = bytesSoFar - lastReported;
                    if (delta > 0)
                    {
                        Interlocked.Add(ref item.DownloadedSizeField, delta);
                        lastReported = bytesSoFar;
                    }
                });

                await using (var output = new FileStream(
                                 temporaryPath,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 bufferSize: 64 * 1024,
                                 useAsync: true))
                {
                    await client.DownloadDecryptedToAsync(info, output, progress, cancellationToken).ConfigureAwait(false);
                }

                string? lyrics = null;
                if (_settings.EmbedLyrics)
                {
                    lyrics = await TryGetLyricsAsync(client, trackId, track, cancellationToken).ConfigureAwait(false);
                }

                try
                {
                    MetadataUtilities.WriteTags(
                        temporaryPath,
                        track,
                        album,
                        item.RemoteAlbum,
                        coverArt,
                        lyrics,
                        Path.GetExtension(fullPath));
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Failed to write tags to temporary track {0}; keeping the downloaded audio without updated tags.", temporaryPath);
                }

                // Same-directory rename keeps the publish step on one filesystem and is
                // atomic on the normal Lidarr filesystems.  Existing files are replaced
                // only after the new track is complete.
                File.Move(temporaryPath, fullPath, overwrite: true);
            }
            finally
            {
                // Cancellation, a broken CDN, a tagging crash or a failed rename must
                // never leave .tmp audio for Lidarr to import later.
                TryDeleteTemporaryFile(temporaryPath);
            }

            if (_settings.DownloadDelayMs > 0)
            {
                await Task.Delay(_settings.DownloadDelayMs, cancellationToken).ConfigureAwait(false);
            }
        }

        private static string BuildTemporaryTrackPath(string albumFolder)
        {
            // Keep the temporary file non-audio by extension so an orphan left after a
            // hard process crash cannot be mistaken for an importable track.  TagLib#
            // is given the real final extension explicitly when tags are written.
            return Path.Combine(albumFolder, $".ym-{Guid.NewGuid():N}.tmp");
        }

        private void TryDeleteTemporaryFile(string temporaryPath)
        {
            try
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warn(ex, "Failed to remove temporary Yandex.Music download {0}.", temporaryPath);
            }
        }

        private async Task<string?> TryGetLyricsAsync(
            YandexMusicClient client,
            long trackId,
            Track track,
            CancellationToken cancellationToken)
        {
            var lyricsInfo = track.LyricsInfo;
            if (lyricsInfo is null || (!lyricsInfo.HasAvailableTextLyrics && !lyricsInfo.HasAvailableSyncLyrics))
            {
                _logger?.Debug("Yandex.Music track {0} ({1}) has no available lyrics.", track.Id, track.Title);
                return null;
            }

            // TEXT is best suited to the common embedded-lyrics tag used by MP3/M4A/FLAC.
            // If Yandex exposes only synchronized lyrics, retain the LRC timestamps in that
            // same field instead of discarding lyrics altogether.
            if (lyricsInfo.HasAvailableTextLyrics)
            {
                try
                {
                    var text = await client.Lyrics
                        .GetAsync(trackId, track.DurationMs, LyricsFormat.Text, cancellationToken)
                        .ConfigureAwait(false);
                    _logger?.Info("Yandex.Music track {0} ({1}): embedded TEXT lyrics ({2} characters).", track.Id, track.Title, text.Length);
                    return text;
                }
                catch (Exception ex)
                {
                    if (!lyricsInfo.HasAvailableSyncLyrics)
                    {
                        _logger?.Warn(ex, "Failed to download lyrics for Yandex.Music track {0} ({1}); continuing without embedded lyrics.", track.Id, track.Title);
                        return null;
                    }

                    _logger?.Warn(ex, "Failed to download TEXT lyrics for Yandex.Music track {0} ({1}); trying synchronized LRC instead.", track.Id, track.Title);
                }
            }

            if (lyricsInfo.HasAvailableSyncLyrics)
            {
                try
                {
                    var lrc = await client.Lyrics
                        .GetAsync(trackId, track.DurationMs, LyricsFormat.Lrc, cancellationToken)
                        .ConfigureAwait(false);
                    _logger?.Info("Yandex.Music track {0} ({1}): embedded LRC lyrics ({2} characters).", track.Id, track.Title, lrc.Length);
                    return lrc;
                }
                catch (Exception ex)
                {
                    _logger?.Warn(ex, "Failed to download synchronized lyrics for Yandex.Music track {0} ({1}); continuing without embedded lyrics.", track.Id, track.Title);
                }
            }

            return null;
        }

        private static string BuildAlbumFolder(string root, Album album)
        {
            var artist = album.Artists is { Count: > 0 } artists ? artists[0].Name : "Unknown Artist";
            return Path.Combine(root, PathSanitizer.Sanitize(artist), PathSanitizer.Sanitize(album.Title));
        }

        private static void MarkFailed(DownloadItem item, string message)
        {
            item.Status = DownloadItemStatus.Failed;
            item.Message = message;
        }

        private static long EstimateTotalSize(IReadOnlyCollection<Track> tracks, YandexQuality quality)
        {
            var bitrateKbps = quality switch
            {
                YandexQuality.Low => 64,
                YandexQuality.Normal => 192,
                YandexQuality.Lossless => 1100,
                _ => 192,
            };
            var totalSeconds = tracks.Sum(track => track.DurationMs) / 1000.0;
            return (long)(totalSeconds * bitrateKbps * 1000 / 8);
        }
    }
}
