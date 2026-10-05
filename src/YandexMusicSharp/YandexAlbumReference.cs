using System.Globalization;
using System.Text.RegularExpressions;

namespace YandexMusicSharp;

/// <summary>
/// Parses Yandex Music album references stored by Lidarr metadata providers.
/// Supports the foreign-id formats used by Lidarr.Plugin.YandexMusic.Metadata
/// as well as public Yandex Music album URLs.
/// </summary>
public static class YandexAlbumReference
{
    private static readonly Regex AlbumUrlRegex = new(
        @"^https?://(?:music\.)?yandex\.(?:ru|com|kz|by|uz)/album/(?<id>\d+)(?:[/?#].*)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ForeignIdRegex = new(
        @"^(?:yandex:album:|ym:album:|ym-album:|yandex-album:)(?<id>\d+)(?::artist:\d+)?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryParseAlbumId(string? value, out long albumId)
    {
        albumId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();

        var match = ForeignIdRegex.Match(value);
        if (!match.Success)
        {
            match = AlbumUrlRegex.Match(value);
        }

        return match.Success
               && long.TryParse(match.Groups["id"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out albumId)
               && albumId > 0;
    }
}
