// -----------------------------------------------------------------------
// <copyright file="PolicySyncService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Governance;
using Netclaw.Registration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Pulls the panel-composed ACS policy set for this daemon on startup and
/// periodically thereafter. The panel (NetFleet, configured under
/// <c>Registration.BaseUrl</c>) answers <c>POST /api/v1/agent/policies/check</c>
/// with the current version hash and, when it differs from what this daemon
/// reports as applied, the full file set. Changed sets are validated and
/// applied atomically by <see cref="AcsPolicyApplier"/> (which hot-reloads
/// the governance runtime); rejections and errors are recorded in the sync
/// state and replayed to the panel on the next poll. Never blocks or crashes
/// the daemon on network failures — the next tick is the retry, and the
/// last-good on-disk policy keeps applying.
/// </summary>
internal sealed class PolicySyncService : BackgroundService
{
    public const string CheckPath = "api/v1/agent/policies/check";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly PolicySyncConfig _config;
    private readonly RegistrationConfig _registrationConfig;
    private readonly PolicySyncStateStore _stateStore;
    private readonly AcsPolicyApplier _applier;
    private readonly DaemonIdentity _daemonIdentity;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<PolicySyncService> _logger;
    private readonly HttpClient _httpClient;

    public PolicySyncService(
        PolicySyncConfig config,
        RegistrationConfig registrationConfig,
        PolicySyncStateStore stateStore,
        AcsPolicyApplier applier,
        DaemonIdentity daemonIdentity,
        TimeProvider timeProvider,
        ILogger<PolicySyncService> logger,
        HttpClient httpClient)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _registrationConfig = registrationConfig ?? throw new ArgumentNullException(nameof(registrationConfig));
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
        _applier = applier ?? throw new ArgumentNullException(nameof(applier));
        _daemonIdentity = daemonIdentity ?? throw new ArgumentNullException(nameof(daemonIdentity));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SyncAsync(stoppingToken);

        var intervalMinutes = Math.Clamp(_config.IntervalMinutes, 0, 1440);
        if (intervalMinutes <= 0)
            return;

        // Stagger the first periodic tick slightly so a fleet of daemons
        // sharing one panel doesn't poll in lockstep.
        var jitter = TimeSpan.FromSeconds(Random.Shared.Next(0, 30));
        await Task.Delay(jitter, _timeProvider, stoppingToken);
        await SyncAsync(stoppingToken);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(intervalMinutes), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SyncAsync(stoppingToken);
        }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        PolicyCheckResponse? response;
        try
        {
            response = await CheckAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Policy sync check against the panel failed; keeping on-disk policies.");
            return;
        }

        if (response is null || !response.Changed || response.Files is null)
        {
            return;
        }

        var state = _stateStore.TryRead();
        var result = _applier.Apply([.. response.Files.Select(f => new PolicySyncFile(f.Name ?? string.Empty, f.Content ?? string.Empty))]);
        if (result.Applied)
        {
            _stateStore.Write(new PolicySyncState(response.Version, _timeProvider.GetUtcNow(), null));
            _logger.LogInformation(
                "Applied panel policy set {Version} ({FileCount} file(s)).",
                ShortVersion(response.Version), response.Files.Count);
        }
        else
        {
            // Keep the previously applied version; replay the rejection to
            // the panel on the next poll so it can surface the error.
            _stateStore.Write(new PolicySyncState(state?.Version, state?.AppliedAtUtc, result.Error));
            _logger.LogWarning(
                "Panel policy set {Version} was rejected: {Error}",
                ShortVersion(response.Version), result.Error);
        }
    }

    private async Task<PolicyCheckResponse?> CheckAsync(CancellationToken ct)
    {
        var baseUrl = _registrationConfig.BaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning("Registration.BaseUrl is not set; policy sync cannot reach the panel.");
            return null;
        }

        var state = _stateStore.TryRead();
        var request = new PolicyCheckRequest(
            _daemonIdentity.DaemonId,
            state?.Version,
            state?.Error);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_config.TimeoutSeconds, 1, 300)));

        var checkUrl = baseUrl.TrimEnd('/') + "/" + CheckPath;
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, checkUrl)
        {
            Content = JsonContent.Create(request, options: JsonOptions)
        };

        using var response = await _httpClient.SendAsync(httpRequest, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await ReadErrorDetailAsync(response, timeoutCts.Token);
            _logger.LogWarning(
                "Policy check returned {StatusCode} from the panel{Detail}; keeping on-disk policies.",
                (int)response.StatusCode, detail);
            return null;
        }

        var check = await response.Content.ReadFromJsonAsync<PolicyCheckResponse>(JsonOptions, timeoutCts.Token);
        if (check is null || string.IsNullOrWhiteSpace(check.Version))
        {
            _logger.LogWarning("Policy check returned a malformed payload; keeping on-disk policies.");
            return null;
        }

        return check;
    }

    private async Task<string> ReadErrorDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            return body.Length == 0 ? string.Empty : ": " + body[..Math.Min(body.Length, 400)];
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return string.Empty;
        }
    }

    private static string ShortVersion(string version) =>
        version.Length <= 12 ? version : version[..12];

    private sealed record PolicyCheckRequest(string InstanceId, string? AppliedVersion, string? LastError);

    private sealed record PolicyCheckResponse(string Version, bool Changed, IReadOnlyList<PolicyCheckFile>? Files);

    private sealed record PolicyCheckFile(string Name, string Content);
}
