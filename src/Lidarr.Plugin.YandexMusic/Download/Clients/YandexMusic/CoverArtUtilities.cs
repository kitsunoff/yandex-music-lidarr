using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NzbDrone.Core.Download.Clients.YandexMusic
{
    internal sealed class CoverArtData
    {
        public CoverArtData(byte[] bytes, string mimeType)
        {
            Bytes = bytes;
            MimeType = mimeType;
        }

        public byte[] Bytes { get; }

        public string MimeType { get; }
    }

    internal static class CoverArtUtilities
    {
        private static readonly HttpClient HttpClient = new();

        public static async Task<CoverArtData?> DownloadAsync(
            string? coverUri,
            YandexMusicCoverResolutionOption resolution,
            CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(coverUri))
            {
                return null;
            }

            var uri = BuildCoverUri(coverUri, resolution);
            using var response = await HttpClient
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                return null;
            }

            var mimeType = response.Content.Headers.ContentType?.MediaType;
            if (string.IsNullOrWhiteSpace(mimeType) || !mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            {
                mimeType = DetectMimeType(bytes);
            }

            return new CoverArtData(bytes, mimeType);
        }

        internal static Uri BuildCoverUri(string coverUri, YandexMusicCoverResolutionOption resolution)
        {
            var size = resolution switch
            {
                YandexMusicCoverResolutionOption.Small => "400x400",
                YandexMusicCoverResolutionOption.Large => "1000x1000",
                YandexMusicCoverResolutionOption.Original => "orig",
                _ => "1000x1000",
            };

            var resolved = coverUri.Replace("%%", size, StringComparison.Ordinal);
            if (resolved.StartsWith("//", StringComparison.Ordinal))
            {
                resolved = "https:" + resolved;
            }
            else if (!resolved.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                     !resolved.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                resolved = "https://" + resolved.TrimStart('/');
            }

            return new Uri(resolved, UriKind.Absolute);
        }

        private static string DetectMimeType(byte[] bytes)
        {
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                return "image/jpeg";
            }

            if (bytes.Length >= 8 &&
                bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 &&
                bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
            {
                return "image/png";
            }

            if (bytes.Length >= 12 &&
                bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F' &&
                bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                return "image/webp";
            }

            return "image/jpeg";
        }
    }
}
