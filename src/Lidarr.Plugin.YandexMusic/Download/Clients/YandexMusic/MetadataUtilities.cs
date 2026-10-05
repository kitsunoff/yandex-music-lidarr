using System.Globalization;
using System.Linq;
using NzbDrone.Core.Parser.Model;
using YandexMusicSharp.Models;
using TagLibFile = TagLib.File;

namespace NzbDrone.Core.Download.Clients.YandexMusic
{
    /// <summary>
    /// Writes audio tags via TagLib# (shipped as TagLibSharp-Lidarr by the host).
    /// Tags downloaded files with the MusicBrainz ids attached to the
    /// <see cref="RemoteAlbum"/> Lidarr handed the download client - those are
    /// what Lidarr's importer matches files against.
    /// </summary>
    internal static class MetadataUtilities
    {
        public static void WriteTags(
            string filePath,
            Track yandexTrack,
            Album yandexAlbum,
            RemoteAlbum remoteAlbum,
            CoverArtData? coverArt,
            string? lyrics,
            string? mediaExtension = null)
        {
            var normalizedExtension = mediaExtension?.TrimStart('.').ToLowerInvariant();
            using var file = string.IsNullOrWhiteSpace(normalizedExtension)
                ? TagLibFile.Create(filePath)
                : TagLibFile.Create(filePath, $"taglib/{normalizedExtension}", TagLib.ReadStyle.Average);
            var tag = file.Tag;

            tag.Title = ComposeTitle(yandexTrack.Title, yandexTrack.Version);
            tag.Album = ComposeTitle(yandexAlbum.Title, yandexAlbum.Version);
            tag.AlbumArtists = ResolveArtistNames(yandexAlbum.Artists);
            tag.Performers = ResolveArtistNames(yandexTrack.Artists);
            tag.TrackCount = (uint)yandexAlbum.TrackCount;

            var position = ResolveTrackPosition(yandexTrack, yandexAlbum);
            if (position.Track > 0)
            {
                tag.Track = (uint)position.Track;
            }
            if (position.Disc > 0)
            {
                tag.Disc = (uint)position.Disc;
            }
            if (yandexAlbum.Volumes is { Count: > 1 } volumes)
            {
                tag.DiscCount = (uint)volumes.Count;
            }

            if (yandexAlbum.Year is { } year)
            {
                tag.Year = (uint)year;
            }

            // Lidarr matches imported files to library albums via the MusicBrainz ids.
            // The release Lidarr sent down already has them resolved.
            if (remoteAlbum.Albums.FirstOrDefault() is { } libraryAlbum)
            {
                tag.MusicBrainzReleaseId = libraryAlbum.ForeignAlbumId;
            }
            if (remoteAlbum.Artist is { ForeignArtistId: { } artistMbid })
            {
                tag.MusicBrainzArtistId = artistMbid;
            }

            tag.Comment = $"https://music.yandex.ru/album/{yandexAlbum.Id.ToString(CultureInfo.InvariantCulture)}/track/{yandexTrack.Id}";

            if (!string.IsNullOrWhiteSpace(lyrics))
            {
                tag.Lyrics = lyrics;
            }

            if (coverArt is not null)
            {
                var picture = new TagLib.Picture(new TagLib.ByteVector(coverArt.Bytes))
                {
                    Type = TagLib.PictureType.FrontCover,
                    MimeType = coverArt.MimeType,
                    Description = "Cover",
                };
                tag.Pictures = new TagLib.IPicture[] { picture };
            }

            file.Save();
        }

        private static string ComposeTitle(string title, string? version)
            => string.IsNullOrWhiteSpace(version) ? title : $"{title} ({version})";

        private static string[] ResolveArtistNames(System.Collections.Generic.IReadOnlyList<Artist>? artists)
        {
            if (artists is null || artists.Count == 0)
            {
                return new[] { "Unknown Artist" };
            }
            return artists.Select(a => a.Name).ToArray();
        }

        private static (int Track, int Disc) ResolveTrackPosition(Track track, Album album)
        {
            if (track.Albums is { Count: > 0 } albums)
            {
                foreach (var albumRef in albums)
                {
                    if (albumRef.Id == album.Id && albumRef.TrackPosition is { } pos && pos.Index > 0)
                    {
                        return (pos.Index, pos.Volume > 0 ? pos.Volume : 1);
                    }
                }
            }

            if (album.Volumes is { Count: > 0 } volumes)
            {
                for (var discIndex = 0; discIndex < volumes.Count; discIndex++)
                {
                    var volume = volumes[discIndex];
                    for (var trackIndex = 0; trackIndex < volume.Count; trackIndex++)
                    {
                        if (string.Equals(volume[trackIndex].Id, track.Id, StringComparison.Ordinal))
                        {
                            return (trackIndex + 1, discIndex + 1);
                        }
                    }
                }
            }

            return (0, 0);
        }
    }
}
