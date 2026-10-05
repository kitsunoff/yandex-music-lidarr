using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using NzbDrone.Core.Parser.Model;
using YandexMusicSharp.Http;
using YandexMusicSharp.Models;

namespace NzbDrone.Core.Indexers.YandexMusic
{
    public class YandexMusicParser : IParseIndexerResponse
    {
        // Three releases per album so Lidarr's quality profile can pick the best one
        // that matches the user's preference.  Lossless requires Yandex Plus on the
        // bound account; lower qualities are always available.
        private static readonly (YandexMusicQualityOption Quality, string Codec, string Container, string TitleSuffix, int Bitrate)[] QualityMatrix =
        {
            (YandexMusicQualityOption.Low,      "AAC",  "64",       "AAC 64",   64),
            (YandexMusicQualityOption.Normal,   "AAC",  "192",      "AAC 192",  192),
            (YandexMusicQualityOption.Lossless, "FLAC", "Lossless", "FLAC",     1411),
        };

        private const string DownloadUrlScheme = "yandexmusic://album";
        private const string InfoUrlBase = "https://music.yandex.ru/album/";

        public YandexMusicIndexerSettings Settings { get; set; } = new();

        public IList<ReleaseInfo> ParseResponse(IndexerResponse indexerResponse)
        {
            if (indexerResponse?.Content is null)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var albums = ParseAlbums(indexerResponse.Content);
            if (albums.Count == 0)
            {
                return Array.Empty<ReleaseInfo>();
            }

            var releases = new List<ReleaseInfo>();
            foreach (var album in albums)
            {
                if (album.Available == false)
                {
                    continue;
                }

                foreach (var release in BuildReleasesForAlbum(album))
                {
                    releases.Add(release);
                }
            }

            return releases;
        }

        private static IReadOnlyList<Album> ParseAlbums(string content)
        {
            // Normal fuzzy search response: { result: { albums: { results: [...] } } }
            try
            {
                var searchEnvelope = JsonSerializer.Deserialize<YandexResponse<SearchResult>>(
                    content,
                    YandexMusicHttpClient.JsonOptions);
                if (searchEnvelope?.Result?.Albums?.Results is { Count: > 0 } searchAlbums)
                {
                    return searchAlbums;
                }
            }
            catch (JsonException)
            {
                // Try the direct album response below.
            }

            // Metadata-aware lookup response from /albums/{id}/with-tracks:
            // { result: { id, title, artists, ... } }
            try
            {
                var albumEnvelope = JsonSerializer.Deserialize<YandexResponse<Album>>(
                    content,
                    YandexMusicHttpClient.JsonOptions);
                if (albumEnvelope?.Result is { Id: > 0 } directAlbum
                    && !string.IsNullOrWhiteSpace(directAlbum.Title))
                {
                    return new[] { directAlbum };
                }
            }
            catch (JsonException)
            {
                // Invalid/unsupported responses simply yield no releases, matching the
                // previous parser behaviour.
            }

            return Array.Empty<Album>();
        }

        private static IEnumerable<ReleaseInfo> BuildReleasesForAlbum(Album album)
        {
            var artistName = ResolveArtistName(album);
            var albumTitle = ComposeTitle(album.Title, album.Version);
            var year = album.Year ?? ParseYear(album.ReleaseDate);
            var publishDate = ParsePublishDate(album.ReleaseDate, album.Year);
            var trackCount = Math.Max(album.TrackCount, album.Volumes?.Sum(v => v.Count) ?? 0);
            trackCount = Math.Max(trackCount, 1);

            foreach (var (quality, codec, container, suffix, bitrate) in QualityMatrix)
            {
                var sizeBytes = EstimateSize(trackCount, bitrate);
                yield return new ReleaseInfo
                {
                    Guid = $"YandexMusic-{album.Id}-{quality}",
                    Artist = artistName,
                    Album = albumTitle,
                    Title = BuildReleaseTitle(artistName, albumTitle, year, suffix),
                    Codec = codec,
                    Container = container,
                    DownloadUrl = $"{DownloadUrlScheme}/{album.Id}/quality/{quality.ToString().ToLowerInvariant()}",
                    InfoUrl = InfoUrlBase + album.Id.ToString(CultureInfo.InvariantCulture),
                    PublishDate = publishDate,
                    Size = sizeBytes,
                    DownloadProtocol = nameof(YandexMusicDownloadProtocol),
                };
            }
        }

        private static string BuildReleaseTitle(string artist, string album, int? year, string qualitySuffix)
        {
            var yearPart = year is null ? string.Empty : $" ({year.Value})";
            return $"{artist} - {album}{yearPart} [{qualitySuffix}] [WEB]";
        }

        private static string ComposeTitle(string title, string? version)
        {
            return string.IsNullOrWhiteSpace(version) ? title : $"{title} ({version})";
        }

        private static string ResolveArtistName(Album album)
        {
            if (album.Artists is { Count: > 0 } artists)
            {
                return artists[0].Name;
            }
            return "Unknown Artist";
        }

        private static int? ParseYear(string? releaseDate)
        {
            if (string.IsNullOrWhiteSpace(releaseDate))
            {
                return null;
            }
            if (DateTimeOffset.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            {
                return dto.Year;
            }
            return null;
        }

        private static DateTime ParsePublishDate(string? releaseDate, int? year)
        {
            if (!string.IsNullOrWhiteSpace(releaseDate)
                && DateTimeOffset.TryParse(releaseDate, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
            {
                return dto.UtcDateTime;
            }
            return year is null ? DateTime.UtcNow : new DateTime(year.Value, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        private static long EstimateSize(int trackCount, int bitrateKbps)
        {
            // Rough estimate: average song length 3.5 minutes. Actual size is determined at
            // download time from the get-file-info response (which includes a precise size).
            const double averageTrackMinutes = 3.5;
            var seconds = trackCount * averageTrackMinutes * 60;
            return (long)(seconds * bitrateKbps * 1000 / 8);
        }
    }
}
