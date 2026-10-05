using System.Net;
using NUnit.Framework;
using YandexMusicSharp.Auth;
using YandexMusicSharp.Codec;
using YandexMusicSharp.Models;
using YandexMusicSharp.Tests.Http;

namespace YandexMusicSharp.Tests;

[TestFixture]
public class CdnFallbackTests
{
    private const string KeyHex = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Test]
    public async Task DownloadDecryptedToAsync_FailedFirstCdn_RollsBackBeforeFallback()
    {
        var plaintext = Enumerable.Range(0, 200_000).Select(i => (byte)(i % 251)).ToArray();
        var ciphertext = AesCtrDecryptor.Decrypt(Convert.FromHexString(KeyHex), plaintext);
        var requestNumber = 0;

        var stub = new StubHttpMessageHandler(_ =>
        {
            requestNumber++;
            Stream body = requestNumber == 1
                ? new ThrowAfterBytesStream(ciphertext, 70_000)
                : new MemoryStream(ciphertext, writable: false);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            };
        });

        using var httpClient = new HttpClient(stub);
        using var sut = new YandexMusicClient(new YandexMusicCredentials("token"), httpClient);
        var info = CreateDownloadInfo();
        using var destination = new MemoryStream();

        await sut.DownloadDecryptedToAsync(info, destination);

        Assert.That(requestNumber, Is.EqualTo(2));
        Assert.That(destination.ToArray(), Is.EqualTo(plaintext),
            "Fallback must replace the partial first CDN payload, not append to it.");
    }

    [Test]
    public async Task DownloadDecryptedToAsync_Fallback_PreservesExistingDestinationPrefix()
    {
        var prefix = new byte[] { 1, 2, 3, 4, 5 };
        var plaintext = Enumerable.Range(0, 120_000).Select(i => (byte)(255 - (i % 251))).ToArray();
        var ciphertext = AesCtrDecryptor.Decrypt(Convert.FromHexString(KeyHex), plaintext);
        var requestNumber = 0;

        var stub = new StubHttpMessageHandler(_ =>
        {
            requestNumber++;
            Stream body = requestNumber == 1
                ? new ThrowAfterBytesStream(ciphertext, 40_000)
                : new MemoryStream(ciphertext, writable: false);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        });

        using var httpClient = new HttpClient(stub);
        using var sut = new YandexMusicClient(new YandexMusicCredentials("token"), httpClient);
        using var destination = new MemoryStream();
        destination.Write(prefix);

        await sut.DownloadDecryptedToAsync(CreateDownloadInfo(), destination);

        Assert.That(destination.ToArray(), Is.EqualTo(prefix.Concat(plaintext).ToArray()));
    }

    private static DownloadInfo CreateDownloadInfo()
    {
        return new DownloadInfo
        {
            Codec = "flac",
            Quality = "lossless",
            Key = KeyHex,
            Urls = new[] { "https://cdn-1.test/audio", "https://cdn-2.test/audio" },
        };
    }

    private sealed class ThrowAfterBytesStream : Stream
    {
        private readonly MemoryStream _inner;
        private readonly long _throwAfter;

        public ThrowAfterBytesStream(byte[] bytes, long throwAfter)
        {
            _inner = new MemoryStream(bytes, writable: false);
            _throwAfter = throwAfter;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_inner.Position >= _throwAfter)
            {
                throw new IOException("Simulated CDN disconnect.");
            }

            var allowed = (int)Math.Min(count, _throwAfter - _inner.Position);
            return _inner.Read(buffer, offset, allowed);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_inner.Position >= _throwAfter)
            {
                throw new IOException("Simulated CDN disconnect.");
            }

            var allowed = (int)Math.Min(buffer.Length, _throwAfter - _inner.Position);
            return await _inner.ReadAsync(buffer[..allowed], cancellationToken);
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
