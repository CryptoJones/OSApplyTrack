// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Aaron K. Clark

using System.Net;
using System.Text;
using ApplyTrack.Api.Data;
using ApplyTrack.Api.Llm;
using Microsoft.Extensions.Logging.Abstractions;

namespace ApplyTrack.Api.Tests;

public class OpenAiCompatibleLlmClientTests
{
    private static readonly EffectiveLlmConfig Cfg =
        new("https://llm.example/v1", "test-model", "test-key", 30);

    [Fact]
    public async Task Parses_a_normal_chat_completion()
    {
        var client = NewClient(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = Json("""{"choices":[{"message":{"content":"  hello from model  "}}]}"""),
        });

        var body = await client.CompleteAsync("system", "user", Cfg);

        Assert.Equal("hello from model", body);
    }

    [Fact]
    public async Task Rejects_success_response_when_declared_content_length_is_too_large()
    {
        var content = new StringContent("{}", Encoding.UTF8, "application/json");
        content.Headers.ContentLength = 1024 * 1024 + 1;
        var client = NewClient(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() =>
            client.CompleteAsync("system", "user", Cfg));

        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public async Task Rejects_success_response_when_stream_exceeds_the_byte_cap()
    {
        var content = new StreamContent(new ChunkyStream(1024 * 1024 + 1));
        content.Headers.ContentType = new("application/json");
        var client = NewClient(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() =>
            client.CompleteAsync("system", "user", Cfg));

        Assert.Contains("too large", ex.Message);
    }

    [Fact]
    public async Task Oversized_error_body_is_not_read_into_memory()
    {
        var content = new StreamContent(new ChunkyStream(8 * 1024));
        content.Headers.ContentType = new("text/plain");
        var client = NewClient(new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = content });

        var ex = await Assert.ThrowsAsync<LlmUnavailableException>(() =>
            client.CompleteAsync("system", "user", Cfg));

        Assert.Equal("the LLM endpoint returned HTTP 502", ex.Message);
    }

    private static OpenAiCompatibleLlmClient NewClient(HttpResponseMessage response) =>
        new(new SingleClientFactory(response), NullLogger<OpenAiCompatibleLlmClient>.Instance);

    private static StringContent Json(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class SingleClientFactory(HttpResponseMessage response) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new SingleResponseHandler(response));
    }

    private sealed class SingleResponseHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class ChunkyStream(long length) : Stream
    {
        private long _remaining = length;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
                return 0;
            var read = (int)Math.Min(count, _remaining);
            Array.Fill<byte>(buffer, (byte)'x', offset, read);
            _remaining -= read;
            return read;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_remaining <= 0)
                return ValueTask.FromResult(0);
            var read = (int)Math.Min(buffer.Length, _remaining);
            buffer[..read].Span.Fill((byte)'x');
            _remaining -= read;
            return ValueTask.FromResult(read);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
