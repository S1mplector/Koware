// Author: Ilgaz Mehmetoğlu
// Tests for resilient manga image downloads.
using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Koware.Cli.Console;
using Xunit;

namespace Koware.Tests;

public class ResilientImageDownloaderTests
{
    [Fact]
    public async Task DownloadImageWithRetryAsync_ValidImage_WritesFileAndCleansTemp()
    {
        var pngPayload = BuildPayloadWithPngHeader(256 * 1024);
        using var handler = new FixedResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(pngPayload)
        });
        using var httpClient = new HttpClient(handler);

        var tempDir = Path.Combine(Path.GetTempPath(), "koware-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "page.png");

        try
        {
            var ok = await httpClient.DownloadImageWithRetryAsync(
                new Uri("https://example.com/page.png"),
                outputPath,
                maxRetries: 1);

            Assert.True(ok);
            Assert.True(File.Exists(outputPath));
            Assert.Equal(pngPayload.LongLength, new FileInfo(outputPath).Length);
            Assert.False(File.Exists(outputPath + ".part"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadImageWithRetryAsync_InvalidPayload_FailsAndRemovesTemp()
    {
        var invalidPayload = System.Text.Encoding.UTF8.GetBytes("this is not image data");
        using var handler = new FixedResponseHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(invalidPayload)
        });
        using var httpClient = new HttpClient(handler);

        var tempDir = Path.Combine(Path.GetTempPath(), "koware-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var outputPath = Path.Combine(tempDir, "page.bin");

        try
        {
            var ok = await httpClient.DownloadImageWithRetryAsync(
                new Uri("https://example.com/page.bin"),
                outputPath,
                maxRetries: 1);

            Assert.False(ok);
            Assert.False(File.Exists(outputPath));
            Assert.False(File.Exists(outputPath + ".part"));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static byte[] BuildPayloadWithPngHeader(int size)
    {
        if (size < 8)
        {
            throw new ArgumentOutOfRangeException(nameof(size), "Payload must be at least 8 bytes.");
        }

        var bytes = new byte[size];
        bytes[0] = 0x89;
        bytes[1] = 0x50;
        bytes[2] = 0x4E;
        bytes[3] = 0x47;
        bytes[4] = 0x0D;
        bytes[5] = 0x0A;
        bytes[6] = 0x1A;
        bytes[7] = 0x0A;

        for (var i = 8; i < size; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    private sealed class FixedResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }
}
