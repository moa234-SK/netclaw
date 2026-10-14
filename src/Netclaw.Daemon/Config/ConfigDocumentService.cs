// -----------------------------------------------------------------------
// <copyright file="ConfigDocumentService.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Daemon.Services;
using Netclaw.Providers;

namespace Netclaw.Daemon.Config;

public sealed record ConfigDocumentView(
    Dictionary<string, object> Config,
    IReadOnlyList<string> RedactedPaths,
    IReadOnlyList<string> ReadOnlyPaths);

/// <summary>A rejected patch: why, and (for read-only touches) which paths.</summary>
public sealed record ConfigPatchRejection(IReadOnlyList<string> Errors, IReadOnlyList<string>? RejectedPaths = null);

/// <summary>An accepted patch. RestartPending is false only for a no-op.</summary>
public sealed record ConfigPatchAcceptance(
    IReadOnlyList<string> ChangedTopLevel,
    bool RestartPending,
    int RestartGeneration);

/// <summary>One PATCH outcome: exactly one of rejection or acceptance is set.</summary>
public sealed record ConfigPatchOutcome(ConfigPatchRejection? Rejection, ConfigPatchAcceptance? Acceptance)
{
    public static ConfigPatchOutcome Reject(ConfigPatchRejection rejection) => new(rejection, null);

    public static ConfigPatchOutcome Accept(ConfigPatchAcceptance acceptance) => new(null, acceptance);
}

/// <summary>
/// Loads, validates, and persists the config document for the HTTP config
/// API. Owns the validate-before-write gate: both files stay untouched when
/// any check fails. The config watcher stays the only reload trigger — this
/// service never mutates runtime config in place.
/// </summary>
public sealed class ConfigDocumentService
{
    private readonly NetclawPaths _paths;
    private readonly IReadOnlyList<ILlmProviderPlugin> _plugins;
    private readonly DaemonRestartSignal _restartSignal;
    private readonly ILogger<ConfigDocumentService> _logger;

    // Serializes patches inside this process so two concurrent PATCHes cannot
    // interleave their read-modify-write cycles. A local CLI write can still
    // race (last write wins, by design).
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public ConfigDocumentService(
        NetclawPaths paths,
        IEnumerable<ILlmProviderPlugin> plugins,
        DaemonRestartSignal restartSignal,
        ILogger<ConfigDocumentService> logger)
    {
        _paths = paths;
        _plugins = plugins.ToList();
        _restartSignal = restartSignal;
        _logger = logger;
    }

    public ConfigDocumentView GetDocument()
    {
        var (config, secrets) = NetclawConfigFiles.LoadConfigFiles(_paths);
        var view = ConfigDocumentPatch.BuildRedactedView(config, secrets, out var redacted);
        return new ConfigDocumentView(view, redacted, ConfigDocumentPatch.ReadOnlyPaths);
    }

    public async Task<ConfigPatchOutcome> PatchAsync(
        JsonElement patchBody,
        ClaimsPrincipal actor,
        CancellationToken cancellationToken)
    {
        Dictionary<string, object?> patch;
        try
        {
            patch = ConfigDocumentPatch.ParseMergePatch(patchBody);
        }
        catch (InvalidOperationException ex)
        {
            return ConfigPatchOutcome.Reject(new ConfigPatchRejection([ex.Message]));
        }

        var touched = ConfigDocumentPatch.CollectTouchedPaths(patch);
        var readOnly = ConfigDocumentPatch.FindReadOnlyTouches(touched);
        if (readOnly.Count > 0)
        {
            return ConfigPatchOutcome.Reject(new ConfigPatchRejection(
                [$"The following paths are read-only over HTTP and can only be changed locally (CLI, TUI, or a file edit): {string.Join(", ", readOnly)}."],
                readOnly));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            return ApplyPatch(patch, actor);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private ConfigPatchOutcome ApplyPatch(Dictionary<string, object?> patch, ClaimsPrincipal actor)
    {
        var (config, secrets) = NetclawConfigFiles.LoadConfigFiles(_paths);
        config = ConfigDocumentPatch.Materialize(config);
        secrets = ConfigDocumentPatch.Materialize(secrets);

        var view = ConfigDocumentPatch.BuildRedactedView(config, secrets, out _);
        var merged = ConfigDocumentPatch.BuildRedactedView(config, secrets, out _);
        ConfigDocumentPatch.ApplyMergePatch(merged, patch);

        var changedTopLevel = ConfigDocumentPatch.ChangedTopLevelSections(view, merged);
        var split = ConfigDocumentPatch.Split(config, secrets, merged);
        var candidateConfig = split.Config;
        candidateConfig["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;

        var configChanged = changedTopLevel.Count > 0
                            || JsonSerializer.Serialize(candidateConfig, NetclawConfigFiles.ConfigFileOptions)
                            != JsonSerializer.Serialize(config, NetclawConfigFiles.ConfigFileOptions);
        var secretsChanged = split.ChangedSecretPaths.Count > 0;

        if (!configChanged && !secretsChanged)
            return ConfigPatchOutcome.Accept(new ConfigPatchAcceptance([], RestartPending: false, _restartSignal.Generation));

        // Validate before persistence. A failure here must leave both files
        // byte-identical to what was there before.
        if (!ConfigSchemaValidation.TryValidate(candidateConfig, out var schemaErrors))
            return ConfigPatchOutcome.Reject(new ConfigPatchRejection(schemaErrors));

        IConfigurationRoot candidateConfiguration;
        try
        {
            candidateConfiguration = BuildCandidateConfiguration(candidateConfig, split.Secrets);
        }
        catch (Exception ex) when (ex is InvalidDataException or FormatException or JsonException)
        {
            return ConfigPatchOutcome.Reject(new ConfigPatchRejection([$"The merged configuration would not load: {ex.Message}"]));
        }

        var check = ModelConfigurationValidation.Check(candidateConfiguration);
        if (check.Valid is not { } valid)
            return ConfigPatchOutcome.Reject(new ConfigPatchRejection([check.Error!]));

        // Same plugin check the watcher runs, so what the endpoint accepts the
        // watcher also accepts on reload.
        if (valid.Validation.Status == ProviderRuntimeStatus.Valid)
        {
            var pluginError = new ProviderPluginFactory(valid.Providers, _plugins).Validate(valid.Models);
            if (pluginError is not null)
                return ConfigPatchOutcome.Reject(new ConfigPatchRejection([pluginError]));
        }

        Audit(actor, view, merged, changedTopLevel, split.ChangedSecretPaths);

        _paths.EnsureDirectoriesExist();
        // Secrets first: extra overlay keys are inert for the old config, and
        // the netclaw.json write is what arms the watcher reload.
        if (secretsChanged)
            NetclawConfigFiles.WriteSecretsFile(_paths, split.Secrets);
        NetclawConfigFiles.WriteConfigFile(_paths.NetclawConfigPath, candidateConfig);

        return ConfigPatchOutcome.Accept(new ConfigPatchAcceptance(changedTopLevel, RestartPending: true, _restartSignal.Generation));
    }

    // The audit trail: actor, verb, changed sections. Security-section changes
    // log at warning with old and new values — the three fields are enums and
    // booleans, never secrets. Secret values never reach any log line.
    private void Audit(
        ClaimsPrincipal actor,
        Dictionary<string, object> before,
        Dictionary<string, object> after,
        IReadOnlyList<string> changedTopLevel,
        IReadOnlyList<string> changedSecretPaths)
    {
        var actorName = actor.Identity?.Name ?? "unknown-operator";
        var touchedSecurity = changedTopLevel.Contains("Security", StringComparer.OrdinalIgnoreCase);
        if (touchedSecurity)
        {
            _logger.LogWarning(
                "Config API: actor {Actor} changed the Security section. Security before: {Before}; after: {After}. Changed sections: {Sections}.",
                actorName,
                SerializeSecurity(before),
                SerializeSecurity(after),
                string.Join(", ", changedTopLevel));
        }
        else
        {
            _logger.LogInformation(
                "Config API: actor {Actor} changed sections: {Sections}. Secret paths changed: {SecretCount}.",
                actorName,
                string.Join(", ", changedTopLevel),
                changedSecretPaths.Count);
        }
    }

    private static string SerializeSecurity(Dictionary<string, object> document)
        => document.TryGetValue("Security", out var security) && security is Dictionary<string, object> fields
            ? string.Join(", ", fields.Select(field => $"{field.Key}={field.Value}"))
            : "(absent)";

    // The daemon reads netclaw.json, then secrets.json, then NETCLAW_ environment
    // variables (DaemonConfigurationSources). Validate the candidate against the
    // same layering from memory, so nothing is written that the daemon cannot read.
    private static IConfigurationRoot BuildCandidateConfiguration(
        Dictionary<string, object> config,
        Dictionary<string, object> secrets)
    {
        using var configStream = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(config, NetclawConfigFiles.ConfigFileOptions));
        using var secretsStream = new MemoryStream(
            JsonSerializer.SerializeToUtf8Bytes(secrets, NetclawConfigFiles.ConfigFileOptions));

        return new ConfigurationBuilder()
            .AddJsonStream(configStream)
            .AddJsonStream(secretsStream)
            .AddEnvironmentVariables("NETCLAW_")
            .Build();
    }
}
