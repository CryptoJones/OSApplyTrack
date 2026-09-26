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

    // A tenant base URL is free text; what reaches the logs is scheme://host[:port] (#346).
    [Theory]
    [InlineData("https://user:s3cret@llm.example/v1/chat/completions", "https://llm.example")]
    [InlineData("http://me:pw@10.0.0.5:8080/v1/chat/completions?key=abc", "http://10.0.0.5:8080")]
    [InlineData("https://llm.example/v1/sk-in-the-path/chat/completions", "https://llm.example")]
    [InlineData("not a url", "(unparseable URL)")]
    public void LogSafeEndpoint_keeps_only_scheme_host_and_port(string url, string expected)
    {
        var safe = OpenAiCompatibleLlmClient.LogSafeEndpoint(url);
        Assert.Equal(expected, safe);
        Assert.DoesNotContain("s3cret", safe);
    }

    [Fact]
    public async Task A_failed_request_never_logs_the_userinfo()
    {
        var log = new ListLogger();
        var client = new OpenAiCompatibleLlmClient(
            new SingleClientFactory(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = Json("{}") }), log);
        var cfg = new EffectiveLlmConfig("https://alice:hunter2@llm.example/v1", "m", null, 30);

        await Assert.ThrowsAsync<LlmUnavailableException>(() => client.CompleteAsync("s", "u", cfg));

        Assert.NotEmpty(log.Lines);
        Assert.All(log.Lines, l => Assert.DoesNotContain("hunter2", l));
        Assert.Contains(log.Lines, l => l.Contains("https://llm.example"));
    }

    private sealed class ListLogger : Microsoft.Extensions.Logging.ILogger<OpenAiCompatibleLlmClient>
    {
        public List<string> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel level) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel level, Microsoft.Extensions.Logging.EventId id,
            TState state, Exception? ex, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, ex) + " " + ex);
    }

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
