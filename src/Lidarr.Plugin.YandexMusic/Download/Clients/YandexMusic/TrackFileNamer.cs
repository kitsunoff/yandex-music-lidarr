using System.Globalization;
using System.Text;
using YandexMusicSharp.Models;

namespace NzbDrone.Core.Download.Clients.YandexMusic
{
    internal static class TrackFileNamer
    {
        // Linux/ext4, XFS, CIFS and most NAS filesystems allow at most 255 bytes
        // in one path component.  Limit the *complete* filename, not only the
        // track title, because the track/disc prefix and extension consume bytes too.
        private const int MaxFileNameBytes = 255;

        public static string BuildFileName(Track track, Album album, DownloadInfo info)
        {
            var position = ResolveTrackPosition(track, album);
            var paddedIndex = position.Track.ToString("00", CultureInfo.InvariantCulture);
            var extension = ResolveExtension(info.Codec);

            // Preserve the historical single-disc filename format, but include the
            // volume number when the release has multiple discs so Disc 1 Track 1
            // and Disc 2 Track 1 can never collide in the same album directory.
            var prefix = album.Volumes is { Count: > 1 }
                ? $"{Math.Max(position.Disc, 1).ToString(CultureInfo.InvariantCulture)}-{paddedIndex}"
                : paddedIndex;

            var reservedBytes = Encoding.UTF8.GetByteCount($"{prefix} - .{extension}");
            var maxTitleBytes = Math.Max(1, MaxFileNameBytes - reservedBytes);
            var title = PathSanitizer.Sanitize(ComposeTitle(track.Title, track.Version), maxTitleBytes);

            return $"{prefix} - {title}.{extension}";
        }

        private static string ComposeTitle(string title, string? version)
            => string.IsNullOrWhiteSpace(version) ? title : $"{title} ({version})";

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

            // Some /with-tracks responses omit trackPosition in the nested album
            // reference.  The outer volumes array still gives us an authoritative
            // disc and track order, so use it as a fallback.
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

            return (0, 1);
        }

        private static string ResolveExtension(string codec) => codec?.ToLowerInvariant() switch
        {
            "flac" => "flac",
            "mp3" => "mp3",
            "aac" or "he-aac" or "aac-mp4" or "he-aac-mp4" or "flac-mp4" => "m4a",
            _ => "audio",
        };
    }
}
