// -----------------------------------------------------------------------
// <copyright file="SelfRegistrationService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Registration;

/// <summary>
/// Mints device pairing codes for the auto-pairing registration flow.
/// Implemented by the daemon over its <c>PairingCodeService</c>; the seam
/// keeps this library free of a reference to the daemon executable,
/// mirroring <see cref="IStatusSnapshotProvider"/>.
/// </summary>
public interface IRegistrationPairingProvider
{
    /// <summary>
    /// Mints a single-use pairing code (<c>XXXX-XXXX</c>) that replaces any
    /// previously pending code. Call only when auto-pairing is active.
    /// </summary>
    string MintPairingCode();
}

/// <summary>
/// Registers the daemon with an external control panel and keeps it warm.
/// The status endpoint receives the full envelope at startup and again
/// whenever the health projection changes; the heartbeat endpoint receives
/// a minimal liveness payload on every other tick. Network failure is a
/// normal runtime condition here: each failure logs a warning and updates
/// the persisted state, and the daemon keeps running. The next tick is the
/// retry.
/// </summary>
public sealed class SelfRegistrationService : BackgroundService
{
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly ILogger<SelfRegistrationService> _logger;
    private readonly RegistrationConfig _config;
    private readonly IStatusSnapshotProvider _statusProvider;
    private readonly IRegistrationPairingProvider _pairingProvider;
    private readonly RegistrationStateStore _stateStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _instanceId;
    private string? _lastProjection;

    public SelfRegistrationService(
        HttpClient httpClient,
        ILogger<SelfRegistrationService> logger,
        RegistrationConfig config,
        IStatusSnapshotProvider statusProvider,
        IRegistrationPairingProvider pairingProvider,
        RegistrationStateStore stateStore,
        TimeProvider timeProvider)
    {
        _httpClient = httpClient;
        _logger = logger;
        _config = config;
        _statusProvider = statusProvider;
        _pairingProvider = pairingProvider;
        _stateStore = stateStore;
        _timeProvider = timeProvider;
        _instanceId = ResolveInstanceId(stateStore);
    }

    /// <summary>Exposed for tests and for logging identity at startup.</summary>
    public string InstanceId => _instanceId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await SendOnceAsync(stoppingToken);

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_config.HeartbeatIntervalSeconds), _timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await SendTickAsync(stoppingToken);
        }
    }

    /// <summary>Startup send: the full envelope to the status endpoint.</summary>
    internal async Task SendOnceAsync(CancellationToken cancellationToken)
    {
        var status = await _statusProvider.GetStatusAsync(cancellationToken);
        _lastProjection = BuildProjection(status);
        var pairingCode = AutoPairActive() ? _pairingProvider.MintPairingCode() : null;
        await PostEnvelopeAsync(status, pairingCode, cancellationToken);
    }

    /// <summary>
    /// Auto-pairing consent gate: the operator must set both
    /// <c>Registration.AutoPair</c> and <c>Registration.ApiBaseUrl</c>.
    /// The validator already rejects the AutoPair-without-URL shape; this
    /// gate is the runtime mirror so the code never ships unless the
    /// panel has an exchange target.
    /// </summary>
    private bool AutoPairActive()
        => _config.AutoPair && !string.IsNullOrWhiteSpace(_config.ApiBaseUrl);

    private async Task SendTickAsync(CancellationToken cancellationToken)
    {
        var status = await _statusProvider.GetStatusAsync(cancellationToken);
        var projection = BuildProjection(status);
        if (projection != _lastProjection)
        {
            // Health transition: the panel needs the fresh full status.
            _lastProjection = projection;
            await PostEnvelopeAsync(status, null, cancellationToken);
            return;
        }

        await PostHeartbeatAsync(cancellationToken);
    }

    private async Task PostEnvelopeAsync(
        DaemonRuntimeStatus.Response status,
        string? pairingCode,
        CancellationToken cancellationToken)
    {
        var requestUri = RegistrationEndpoints.StatusUri(_config.BaseUrl!);
        var envelope = new RegistrationEnvelope
        {
            InstanceId = _instanceId,
            RegisteredAtUtc = _timeProvider.GetUtcNow(),
            Status = status,
            ApiBaseUrl = string.IsNullOrWhiteSpace(_config.ApiBaseUrl)
                ? null
                : _config.ApiBaseUrl.Trim(),
            PairingCode = pairingCode,
        };

        await PostAsync(
            "status endpoint",
            requestUri,
            () => _httpClient.PostAsJsonAsync(requestUri, envelope, JsonOptions, cancellationToken),
            cancellationToken);
    }

    private async Task PostHeartbeatAsync(CancellationToken cancellationToken)
    {
        var requestUri = RegistrationEndpoints.HeartbeatUri(_config.BaseUrl!);
        var payload = new HeartbeatPayload
        {
            InstanceId = _instanceId,
            Timestamp = _timeProvider.GetUtcNow(),
        };

        await PostAsync(
            "heartbeat endpoint",
            requestUri,
            () => _httpClient.PostAsJsonAsync(requestUri, payload, JsonOptions, cancellationToken),
            cancellationToken);
    }

    private async Task PostAsync(
        string endpointLabel,
        Uri requestUri,
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await send();

            var now = _timeProvider.GetUtcNow();
            if (response.IsSuccessStatusCode)
            {
                TryWriteState(new RegistrationState(now, RegistrationState.StateRegistered, null, _instanceId));
                _logger.LogInformation(
                    "Self-registration send to the {EndpointLabel} delivered.", endpointLabel);
                return;
            }

            // Controlled error string — never echoes the configured URL.
            var httpError = $"{endpointLabel}: HTTP {(int)response.StatusCode}";
            TryWriteState(new RegistrationState(now, RegistrationState.StateFailed, httpError, _instanceId));
            _logger.LogWarning(
                "Self-registration failed at the {EndpointLabel}: the control panel answered {StatusCode}.",
                endpointLabel,
                (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var sanitized = SanitizeError(ex, _config.BaseUrl!, requestUri);
            TryWriteState(new RegistrationState(
                _timeProvider.GetUtcNow(), RegistrationState.StateFailed, sanitized, _instanceId));
            _logger.LogWarning(ex, "Self-registration send to the {EndpointLabel} failed — continuing normally.",
                endpointLabel);
        }
    }

    /// <summary>
    /// A state-file write failure (disk full, permissions) must not take the
    /// daemon down; the send outcome is already logged at that point.
    /// </summary>
    private void TryWriteState(RegistrationState state)
    {
        try
        {
            _stateStore.Write(state);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not persist the registration state file.");
        }
    }

    /// <summary>
    /// Health fingerprint that decides whether a tick is a heartbeat or a
    /// status change. Volatile fields (pid, uptime, counters, timestamps)
    /// are deliberately excluded so ordinary churn does not trigger sends.
    /// </summary>
    internal static string BuildProjection(DaemonRuntimeStatus.Response status)
        => string.Join(
            '|',
            status.Overall,
            status.Build.Version,
            string.Join(',', status.Connectors
                .OrderBy(static c => c.Key, StringComparer.Ordinal)
                .Select(static c => $"{c.Key}:{c.Status}")),
            status.Model is { } model ? $"model-degraded:{model.Degraded}" : "model:none",
            status.Governance is { } governance ? $"agent-state:{governance.AgentState}" : "agent-state:none");

    /// <summary>
    /// Strips the base URL, the full request URI, and the host from an
    /// exception message so the persisted errorDetail never leaks the
    /// registration endpoint.
    /// </summary>
    internal static string SanitizeError(Exception ex, string baseUrl, Uri requestUri)
    {
        var message = $"{ex.GetType().Name}: {ex.Message}";
        if (Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var baseUri))
            message = message.Replace(baseUri.Authority, "[host]", StringComparison.OrdinalIgnoreCase);

        message = message.Replace(requestUri.ToString(), "[url]", StringComparison.OrdinalIgnoreCase);
        message = message.Replace(requestUri.Authority, "[host]", StringComparison.OrdinalIgnoreCase);

        return message.Length > 400 ? message[..400] : message;
    }

    /// <summary>
    /// The panel dedupes daemons by instance ID, so the ID must survive
    /// restarts. The machine name does that; a rare empty machine name falls
    /// back to a random ID persisted inside the state record.
    /// Public so the daemon can use the same identity for agent-level
    /// governance classification (AGT agent id = registration instance id).
    /// </summary>
    public static string ResolveInstanceId(RegistrationStateStore stateStore)
    {
        var hostname = Environment.MachineName?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(hostname))
            return hostname;

        if (stateStore.TryRead()?.InstanceId is { Length: > 0 } persisted)
            return persisted;

        return $"netclaw-{Guid.NewGuid().ToString("N")[..12]}";
    }
}
