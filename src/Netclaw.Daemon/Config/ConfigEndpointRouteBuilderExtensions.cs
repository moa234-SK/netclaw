// -----------------------------------------------------------------------
// <copyright file="ConfigEndpointRouteBuilderExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.RateLimiting;
using Netclaw.Configuration;
using Netclaw.Daemon.Config;

namespace Netclaw.Daemon;

public static class ConfigEndpointRouteBuilderExtensions
{
    internal const string ConfigWriteRateLimitPolicy = "config-write";

    public static IEndpointRouteBuilder MapConfigEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/config", (ConfigDocumentService document) =>
            {
                var view = document.GetDocument();
                return TypedResults.Ok(new ConfigGetResponse(
                    view.Config,
                    view.RedactedPaths,
                    view.ReadOnlyPaths));
            })
            .WithName("GetConfig")
            .WithSummary("Read the daemon configuration document. Every secret stored in secrets.json is replaced by a placeholder; redactedPaths lists them. readOnlyPaths cannot be changed through this API.")
            .WithTags("Config")
            .RequireAuthorization();

        app.MapPatch("/api/config", async ValueTask<Results<Ok<ConfigPatchResponse>, BadRequest<ConfigPatchErrorResponse>>> (
            HttpRequest request,
            ConfigDocumentService document,
            HttpContext httpContext,
            CancellationToken ct) =>
            {
                JsonElement body;
                try
                {
                    body = await request.ReadFromJsonAsync<JsonElement>(ct);
                }
                catch (JsonException ex)
                {
                    return TypedResults.BadRequest(new ConfigPatchErrorResponse(
                        [$"The request body is not valid JSON: {ex.Message}"]));
                }

                var outcome = await document.PatchAsync(body, httpContext.User, ct);
                if (outcome.Rejection is { } rejection)
                {
                    return TypedResults.BadRequest(new ConfigPatchErrorResponse(
                        rejection.Errors,
                        rejection.RejectedPaths));
                }

                var acceptance = outcome.Acceptance!;
                return TypedResults.Ok(new ConfigPatchResponse(
                    acceptance.ChangedTopLevel,
                    acceptance.RestartPending,
                    acceptance.RestartGeneration));
            })
            .WithName("PatchConfig")
            .WithSummary("Apply an RFC 7386 JSON merge patch to the configuration document. null deletes a key; a placeholder at a secret path keeps the stored value; a plain string at a secret path rotates it. Validation failures write nothing and return 400. An accepted patch triggers a coordinated restart: poll /api/health/ready and compare X-Netclaw-Generation.")
            .WithTags("Config")
            .RequireAuthorization()
            .RequireRateLimiting(ConfigWriteRateLimitPolicy);

        return app;
    }

    /// <summary>
    /// A short load-shed window on config writes: an operator edits at human
    /// speed, and a runaway client must not thrash the daemon into restart
    /// loops. Partitioned by user so one device cannot starve another.
    /// </summary>
    public static void AddConfigWriteRateLimitPolicy(this RateLimiterOptions options)
        => options.AddPolicy(ConfigWriteRateLimitPolicy, context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.User.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 10,
                    Window = TimeSpan.FromMinutes(1),
                    QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                    QueueLimit = 0,
                }));
}

public sealed record ConfigGetResponse(
    Dictionary<string, object> Config,
    IReadOnlyList<string> RedactedPaths,
    IReadOnlyList<string> ReadOnlyPaths);

public sealed record ConfigPatchResponse(
    IReadOnlyList<string> ChangedTopLevel,
    bool RestartPending,
    int RestartGeneration);

public sealed record ConfigPatchErrorResponse(
    IReadOnlyList<string> Errors,
    IReadOnlyList<string>? RejectedPaths = null);
