// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleDescriptor.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Configuration;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Provider descriptor for OpenAI-compatible endpoints such as llama.cpp,
/// llama-server, vLLM, Lemonade, or DwarfStar (ds4).
/// </summary>
public sealed class OpenAiCompatibleDescriptor : IProviderDescriptor
{
    private readonly HttpClient _httpClient;

    public OpenAiCompatibleDescriptor(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public string TypeKey => "openai-compatible";
    public string DisplayName => "OpenAI-compatible (llama.cpp / vLLM / DwarfStar ds4)";
    public string DefaultEndpoint => "http://localhost:11434";
    // Unversioned default. The probe resolves the effective path per
    // endpoint via OpenAiCompatibleEndpoint.RelativeModelsPath because a
    // base that already pins a version (…/v4) must not get another "/v1".
    public string ModelListingPath => "/v1/models";
    // Endpoint-only by default, but an operator may supply a Bearer key for
    // gateways that sit in front of a protected vLLM / llama.cpp / SGLang
    // endpoint. The clients send the key when the operator selects API-key auth.
    // This declaration lets each setup surface collect the optional key.
    public IProviderAuth Auth { get; } = new OptionalApiKeyAuth();

    public Task<ProviderProbeResult> ProbeAsync(
        ProviderEntry entry, CancellationToken ct = default)
    {
        // Same endpoint normalisation as chat: the configured endpoint may or may not
        // end in /v1, so the models path comes from OpenAiCompatibleEndpoint rather
        // than being appended to the raw string.
        var endpoint = OpenAiCompatibleEndpoint.FromBaseUrl(
            string.IsNullOrWhiteSpace(entry.Endpoint) ? DefaultEndpoint : entry.Endpoint);

        // Fail loud on malformed vendor headers before any wire work: the
        // operator configured them for every request kind, so a probe must
        // not send one without them.
        if (!OpenAiCompatibleRequestHeaders.TryResolve(entry, out var requestHeaders, out var headerError))
            return Task.FromResult(new ProviderProbeResult(false, headerError, []));

        return ProbeHelpers.ExecuteProbeAsync(
            _httpClient,
            TypeKey,
            DefaultEndpoint,
            endpoint.ModelsPath,
            endpoint.BaseUri.GetLeftPart(UriPartial.Authority),
            request =>
            {
                OpenAiCompatibleHttp.ApplyBearerAuth(
                    request,
                    entry.AuthMethod is AuthMethod.ApiKey ? entry.ApiKey?.Value : null);
                OpenAiCompatibleRequestHeaders.Apply(request, requestHeaders);
            },
            ParseModels,
            ct,
            timeout: ProbeTimeouts.SelfHosted);
    }

    internal static ProviderProbeResult ParseModels(string json)
        => ProbeHelpers.ParseOpenAiStyleModels(json, TryReadContextWindow);

    private static int? TryReadContextWindow(JsonElement model)
    {
        var contextWindow = ProbeHelpers.TryReadPositiveInt32(model, "max_model_len"); // vLLM
        if (contextWindow is not null)
            return contextWindow;

        // ds4 (and other OpenRouter-shaped backends): context_length /
        // top_provider.context_length.
        contextWindow = Ds4BackendStrategy.ReadContextLength(model);
        if (contextWindow is not null)
            return contextWindow;

        return model.TryGetProperty("meta", out var meta) // llama.cpp
            ? ProbeHelpers.TryReadPositiveInt32OrFallbackWhenMissing(
                meta, "n_ctx", "n_ctx_train")
            : null;
    }
}
