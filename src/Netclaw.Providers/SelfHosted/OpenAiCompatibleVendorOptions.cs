// -----------------------------------------------------------------------
// <copyright file="OpenAiCompatibleVendorOptions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.RegularExpressions;
using Netclaw.Configuration;
using Netclaw.Configuration.Providers;

namespace Netclaw.Providers.SelfHosted;

/// <summary>
/// Typed view of <c>Providers:&lt;name&gt;:VendorOptions</c> for the
/// <c>openai-compatible</c> plugin. <see cref="Headers"/> names static HTTP
/// headers applied to every request sent to the endpoint (chat completions,
/// model listing, capability probes, provider probe).
/// </summary>
/// <remarks>
/// Use case: OpenAI-compatible fronting proxies that require routing headers.
/// Example: TencentDB Agent Memory MemoryProxy, which registers a session from
/// <c>x-team-id</c> / <c>x-agent-id</c> / <c>x-task-id</c> /
/// <c>x-conversation-id</c> headers instead of an interactive form.
/// </remarks>
public sealed class OpenAiCompatibleVendorOptions : IVendorOptions
{
    /// <summary>Header name to header value. Names are case-insensitive on the wire.</summary>
    public Dictionary<string, string>? Headers { get; set; }
}

/// <summary>
/// Parses, validates, and applies the static request headers declared in
/// <see cref="OpenAiCompatibleVendorOptions"/>. Shared by every component that
/// sends HTTP on behalf of an <c>openai-compatible</c> provider entry so the
/// proxy in front of the endpoint sees the same headers on all request kinds.
/// </summary>
public static partial class OpenAiCompatibleRequestHeaders
{
    // Authorization is owned by the ApiKey credential, and Content-Type /
    // Content-Length / Host are owned by the transport. Letting config override
    // them would silently change auth or request framing, so they are rejected
    // loudly instead (no-silent-bypass rule).
    private static readonly HashSet<string> ReservedHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "authorization",
            "content-type",
            "content-length",
            "host"
        };

    // RFC 9110 token grammar for header names — no spaces, no controls, ASCII only.
    [GeneratedRegex("^[!#$%&'*+\\-.^_`|~0-9A-Za-z]+$")]
    private static partial Regex HeaderNameRegex();

    /// <summary>
    /// Resolves the validated header map from a provider entry's vendor options.
    /// Succeeds with <c>null</c> headers when none are configured. Fails when
    /// the vendor options block is malformed or a header fails validation.
    /// </summary>
    public static bool TryResolve(
        ProviderEntry entry,
        out IReadOnlyDictionary<string, string>? headers,
        out string? error)
    {
        headers = null;
        error = null;

        OpenAiCompatibleVendorOptions? options;
        try
        {
            options = entry.GetVendorOptions<OpenAiCompatibleVendorOptions>();
        }
        catch (InvalidOperationException ex)
        {
            error = $"Providers VendorOptions are invalid for provider type '{entry.Type}': {ex.Message}";
            return false;
        }

        if (options?.Headers is not { Count: > 0 } map)
            return true;

        return TryValidate(map, out headers, out error);
    }

    /// <summary>
    /// Validates a caller-supplied header map (CLI flags, tests) using the same
    /// rules as persisted vendor options. Returns an ordinal-case-insensitive
    /// dictionary so wire comparison is stable.
    /// </summary>
    public static bool TryValidate(
        IReadOnlyDictionary<string, string> map,
        out IReadOnlyDictionary<string, string> headers,
        out string? error)
    {
        var validated = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        headers = validated;
        error = null;

        foreach (var (name, value) in map)
        {
            if (string.IsNullOrWhiteSpace(name) || !HeaderNameRegex().IsMatch(name))
            {
                error = $"'{name}' is not a valid HTTP header name.";
                return false;
            }

            if (string.IsNullOrEmpty(value))
            {
                error = $"Header '{name}' has an empty value.";
                return false;
            }

            if (value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal))
            {
                // Reject header injection outright — a newline in a header value
                // can smuggle an extra header to the upstream.
                error = $"Header '{name}' contains a line break.";
                return false;
            }

            if (ReservedHeaders.Contains(name))
            {
                error = $"Header '{name}' is reserved. Use the provider ApiKey for Authorization; "
                    + "content framing headers are transport-owned.";
                return false;
            }

            if (!validated.TryAdd(name, value))
            {
                error = $"Header '{name}' is declared more than once (names are case-insensitive).";
                return false;
            }
        }

        return true;
    }

    /// <summary>Applies validated headers to a request. No-ops when null/empty.</summary>
    public static void Apply(HttpRequestMessage request, IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0)
            return;

        foreach (var (name, value) in headers)
            request.Headers.TryAddWithoutValidation(name, value);
    }
}
