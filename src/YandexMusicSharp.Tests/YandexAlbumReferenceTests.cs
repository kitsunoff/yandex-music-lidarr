using NUnit.Framework;
using YandexMusicSharp;

namespace YandexMusicSharp.Tests;

[TestFixture]
public sealed class YandexAlbumReferenceTests
{
    [TestCase("yandex:album:123456", 123456L)]
    [TestCase("yandex:album:123456:artist:987", 123456L)]
    [TestCase("ym:album:123456", 123456L)]
    [TestCase("ym-album:123456", 123456L)]
    [TestCase("yandex-album:123456", 123456L)]
    [TestCase("https://music.yandex.ru/album/123456", 123456L)]
    [TestCase("https://music.yandex.com/album/123456?utm_source=test", 123456L)]
    [TestCase("https://yandex.kz/album/123456/track/55", 123456L)]
    public void ParsesSupportedAlbumReferences(string value, long expected)
    {
        Assert.That(YandexAlbumReference.TryParseAlbumId(value, out var actual), Is.True);
        Assert.That(actual, Is.EqualTo(expected));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("123456")]
    [TestCase("yandex:artist:123456")]
    [TestCase("https://music.yandex.ru/artist/123456")]
    [TestCase("https://example.com/album/123456")]
    public void RejectsNonAlbumReferences(string? value)
    {
        Assert.That(YandexAlbumReference.TryParseAlbumId(value, out _), Is.False);
    }
}
