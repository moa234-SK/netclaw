// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleVendorOptionsTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Netclaw.Configuration;
using Netclaw.Providers.SelfHosted;
using Xunit;

namespace Netclaw.Daemon.Tests.Providers;

public sealed class OpenAiCompatibleVendorOptionsTests
{
    private static ProviderEntry EntryWithHeaders(JsonObject headers)
    {
        var entry = new ProviderEntry
        {
            Type = "openai-compatible",
            Endpoint = "http://localhost:8096/openclaw/default/v1"
        };
        entry.SetVendorOptions(new JsonObject { ["Headers"] = headers });
        return entry;
    }

    [Fact]
    public void TryResolve_BindsHeaders_FromPersistedVendorOptionsShape()
    {
        var entry = EntryWithHeaders(new JsonObject
        {
            ["x-team-id"] = "team-1",
            ["x-agent-id"] = "agent-1",
            ["x-task-id"] = "task-1",
            ["x-conversation-id"] = "session-1"
        });

        var ok = OpenAiCompatibleRequestHeaders.TryResolve(entry, out var headers, out var error);

        Assert.True(ok, error);
        Assert.NotNull(headers);
        Assert.Equal("team-1", headers!["x-team-id"]);
        Assert.Equal("agent-1", headers["x-agent-id"]);
        Assert.Equal("task-1", headers["x-task-id"]);
        Assert.Equal("session-1", headers["x-conversation-id"]);
    }

    [Fact]
    public void TryResolve_NoVendorOptions_SucceedsWithNullHeaders()
    {
        var entry = new ProviderEntry { Type = "openai-compatible" };

        var ok = OpenAiCompatibleRequestHeaders.TryResolve(entry, out var headers, out var error);

        Assert.True(ok);
        Assert.Null(error);
        Assert.Null(headers);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("authorization")]
    [InlineData("Content-Type")]
    [InlineData("Host")]
    public void TryValidate_RejectsReservedHeaders(string name)
    {
        var ok = OpenAiCompatibleRequestHeaders.TryValidate(
            new Dictionary<string, string> { [name] = "value" },
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("reserved", error);
    }

    [Fact]
    public void TryValidate_RejectsLineBreakInHeaderValue()
    {
        var ok = OpenAiCompatibleRequestHeaders.TryValidate(
            new Dictionary<string, string> { ["x-team-id"] = "t1\r\nX-Injected: yes" },
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("line break", error);
    }

    [Fact]
    public void TryValidate_RejectsCaseInsensitiveDuplicate()
    {
        var ok = OpenAiCompatibleRequestHeaders.TryValidate(
            new Dictionary<string, string>
            {
                ["x-team-id"] = "t1",
                ["X-Team-Id"] = "t2"
            },
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("more than once", error);
    }

    [Theory]
    [InlineData("x team")]
    [InlineData("x-team:id")]
    [InlineData("")]
    public void TryValidate_RejectsInvalidHeaderNames(string name)
    {
        var ok = OpenAiCompatibleRequestHeaders.TryValidate(
            new Dictionary<string, string> { [name] = "value" },
            out _,
            out var error);

        Assert.False(ok);
        Assert.Contains("not a valid HTTP header name", error);
    }

    [Fact]
    public void TryResolve_RejectsNonStringHeaderValue()
    {
        var entry = EntryWithHeaders(new JsonObject { ["x-team-id"] = 42 });

        var ok = OpenAiCompatibleRequestHeaders.TryResolve(entry, out _, out var error);

        Assert.False(ok);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public async Task Probe_SendsConfiguredHeadersAlongsideBearerAuth()
    {
        HttpRequestMessage? captured = null;
        using var handler = new RecordingHandler(request =>
        {
            captured = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"data\":[{\"id\":\"m1\"}]}", Encoding.UTF8, "application/json")
            };
        });
        using var httpClient = new HttpClient(handler);
        var descriptor = new OpenAiCompatibleDescriptor(httpClient);
        var entry = EntryWithHeaders(new JsonObject { ["x-team-id"] = "team-1" });
        entry.ApiKey = new SensitiveString("sk-test");

        var result = await descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(captured);
        Assert.Equal("team-1", captured!.Headers.GetValues("x-team-id").Single());
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("sk-test", captured.Headers.Authorization?.Parameter);
    }

    [Fact]
    public async Task Probe_InvalidHeaders_FailsWithoutSendingRequest()
    {
        using var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var descriptor = new OpenAiCompatibleDescriptor(httpClient);
        var entry = EntryWithHeaders(new JsonObject { ["Authorization"] = "Bearer injected" });

        var result = await descriptor.ProbeAsync(entry, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("reserved", result.ErrorMessage);
        Assert.Empty(handler.Requests);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(responder(request));
        }
    }
}
