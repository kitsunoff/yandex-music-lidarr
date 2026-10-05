using System.Net;
using System.Text;
using NUnit.Framework;
using YandexMusicSharp.Api;
using YandexMusicSharp.Auth;
using YandexMusicSharp.Http;
using YandexMusicSharp.Models;
using YandexMusicSharp.Tests.Http;

namespace YandexMusicSharp.Tests.Api;

[TestFixture]
public class LyricsClientTests
{
    [Test]
    public async Task GetAsync_BuildsSignedRequest_AndDownloadsText()
    {
        const long timestamp = 1_700_000_000L;
        const long trackId = 565_378L;
        const long durationMs = 181_680L;
        var expectedSignature = RequestSigner.SignLyrics(trackId, timestamp);

        var call = 0;
        var stub = new StubHttpMessageHandler(request =>
        {
            call++;
            if (call == 1)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"result":{"downloadUrl":"https://lyrics.example/track.txt","lyricId":1}}""",
                        Encoding.UTF8,
                        "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("Line one\nLine two", Encoding.UTF8, "text/plain"),
            };
        });

        using var httpClient = new HttpClient(stub);
        using var http = new YandexMusicHttpClient(new YandexMusicCredentials("t"), httpClient);
        var sut = new LyricsClient(http, () => timestamp);

        var lyrics = await sut.GetAsync(trackId, durationMs, LyricsFormat.Text);

        Assert.That(lyrics, Is.EqualTo("Line one\nLine two"));
        Assert.That(stub.Requests, Has.Count.EqualTo(2));

        var url = stub.Requests[0].RequestUri!.AbsoluteUri;
        Assert.That(url, Does.Contain($"/tracks/{trackId}/lyrics?"));
        Assert.That(url, Does.Contain("format=TEXT"));
        Assert.That(url, Does.Contain($"durationMs={durationMs}"));
        Assert.That(url, Does.Contain($"timeStamp={timestamp}"));
        Assert.That(url, Does.Contain($"sign={Uri.EscapeDataString(expectedSignature)}"));
        Assert.That(stub.Requests[1].RequestUri!.AbsoluteUri, Is.EqualTo("https://lyrics.example/track.txt"));
    }

    [Test]
    public async Task GetAsync_UsesLrcFormat_WhenRequested()
    {
        var call = 0;
        var stub = new StubHttpMessageHandler(request =>
        {
            call++;
            return call == 1
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"result":{"downloadUrl":"https://lyrics.example/track.lrc"}}""",
                        Encoding.UTF8,
                        "application/json"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("[00:01.00]Line", Encoding.UTF8, "text/plain"),
                };
        });

        using var httpClient = new HttpClient(stub);
        using var http = new YandexMusicHttpClient(new YandexMusicCredentials("t"), httpClient);
        var sut = new LyricsClient(http, () => 123);

        var lyrics = await sut.GetAsync(456, 10_000, LyricsFormat.Lrc);

        Assert.That(lyrics, Is.EqualTo("[00:01.00]Line"));
        Assert.That(stub.Requests[0].RequestUri!.AbsoluteUri, Does.Contain("format=LRC"));
    }
}
