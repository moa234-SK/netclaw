// -----------------------------------------------------------------------
// <copyright file="RegistrationConfigViewModel.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using Netclaw.Cli.Config;
using Netclaw.Configuration;
using Netclaw.Registration;
using R3;
using Termina.Reactive;

namespace Netclaw.Cli.Tui.Config;

/// <summary>
/// Registration section editor. Rows: enabled toggle, control panel URL,
/// heartbeat interval. Validation runs through the shared
/// <see cref="RegistrationConfigValidator"/> so this editor rejects exactly
/// what daemon startup and doctor reject, before anything is persisted.
/// A blank URL is valid — it is the feature's off state.
/// </summary>
internal sealed class RegistrationConfigViewModel : ReactiveViewModel
{
    internal const int EnabledRow = 0;
    internal const int BaseUrlRow = 1;
    internal const int IntervalRow = 2;
    internal const int RowCount = 3;

    private readonly NetclawPaths _paths;
    private string _acceptedBaseUrl;

    public RegistrationConfigViewModel(NetclawPaths paths)
    {
        _paths = paths;
        // Degrade to defaults on a malformed/unreadable netclaw.json rather than
        // throwing from the constructor (which would make the page inaccessible).
        string? loadError = null;
        (bool Enabled, string BaseUrl, string Interval) state;
        try
        {
            state = LoadState(paths);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            state = (true, string.Empty, string.Empty);
            loadError = $"Could not read netclaw.json: {ex.Message}";
        }

        _acceptedBaseUrl = state.BaseUrl;
        Enabled = new ReactiveProperty<bool>(state.Enabled);
        BaseUrlDraft = new ReactiveProperty<string>(state.BaseUrl);
        HeartbeatIntervalDraft = new ReactiveProperty<string>(state.Interval);
        SelectedIndex = new ReactiveProperty<int>(0);
        Status = new ReactiveProperty<ConfigStatusMessage>(loadError is null
            ? new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral)
            : new ConfigStatusMessage(loadError, ConfigStatusTone.Error));
        IsSaved = new ReactiveProperty<bool>(false);
    }

    internal Action<string>? RouteRequested { get; set; }
    internal bool ShutdownRequestedForTest { get; private set; }

    public ReactiveProperty<bool> Enabled { get; }
    public ReactiveProperty<string> BaseUrlDraft { get; }
    public ReactiveProperty<string> HeartbeatIntervalDraft { get; }
    public ReactiveProperty<int> SelectedIndex { get; }
    public ReactiveProperty<ConfigStatusMessage> Status { get; }
    public ReactiveProperty<bool> IsSaved { get; }

    public void MoveSelection(int delta)
    {
        var next = Math.Clamp(SelectedIndex.Value + delta, 0, RowCount - 1);
        if (next != SelectedIndex.Value)
            SelectedIndex.Value = next;
    }

    public void ActivateSelected()
    {
        switch (SelectedIndex.Value)
        {
            case EnabledRow:
                ToggleEnabled();
                break;
            default:
                Save();
                break;
        }
    }

    /// <summary>Toggles registration enablement from row 0 and autosaves.</summary>
    public bool ToggleEnabled()
    {
        var previous = Enabled.Value;
        Enabled.Value = !Enabled.Value;
        if (Save("Registration enabled state saved."))
            return true;

        Enabled.Value = previous;
        IsSaved.Value = false;
        RequestRedraw();
        return false;
    }

    public void AppendText(string text)
    {
        if (SelectedIndex.Value == BaseUrlRow)
        {
            if (BaseUrlDraft.Value == _acceptedBaseUrl)
                BaseUrlDraft.Value = string.Empty;

            BaseUrlDraft.Value += text;
        }
        else if (SelectedIndex.Value == IntervalRow)
        {
            HeartbeatIntervalDraft.Value += text;
        }
        else
        {
            return;
        }

        MarkDirty();
    }

    public void Backspace()
    {
        if (SelectedIndex.Value == BaseUrlRow && BaseUrlDraft.Value.Length > 0)
        {
            BaseUrlDraft.Value = BaseUrlDraft.Value[..^1];
            MarkDirty();
        }
        else if (SelectedIndex.Value == IntervalRow && HeartbeatIntervalDraft.Value.Length > 0)
        {
            HeartbeatIntervalDraft.Value = HeartbeatIntervalDraft.Value[..^1];
            MarkDirty();
        }
    }

    public bool Save() => Save("Registration settings saved.");

    private bool Save(string successMessage)
    {
        // Blank interval keeps the schema default (300); a blank URL is the off state.
        var baseUrl = BaseUrlDraft.Value.Trim();
        var intervalText = HeartbeatIntervalDraft.Value.Trim();
        var interval = string.IsNullOrWhiteSpace(intervalText)
            ? 300
            : (int.TryParse(intervalText, out var parsed) ? parsed : 0);

        var candidate = new RegistrationConfig
        {
            BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? null : baseUrl,
            Enabled = Enabled.Value,
            HeartbeatIntervalSeconds = interval,
        };

        if (!RegistrationConfigValidator.TryValidate(candidate, out var error))
        {
            Status.Value = new ConfigStatusMessage(error, ConfigStatusTone.Error);
            RequestRedraw();
            return false;
        }

        return ConfigAutosave.Run(
            () =>
            {
                var root = ConfigFileHelper.LoadJsonDict(_paths.NetclawConfigPath);
                root["configVersion"] = EmbeddedSchemaLoader.CurrentSchemaVersion;

                // This page edits three rows but owns the whole section —
                // preserve values it does not expose (ApiBaseUrl is set by
                // hand per the panel-command-channel contract) instead of
                // dropping them on save.
                var preservedApiBaseUrl = root.TryGetValue("Registration", out var existingSection)
                    && existingSection is Dictionary<string, object?> sectionDict
                    && sectionDict.TryGetValue("ApiBaseUrl", out var preserved)
                    ? preserved
                    : null;

                root["Registration"] = new Dictionary<string, object?>
                {
                    ["BaseUrl"] = candidate.BaseUrl,
                    ["Enabled"] = candidate.Enabled,
                    ["HeartbeatIntervalSeconds"] = candidate.HeartbeatIntervalSeconds,
                    ["ApiBaseUrl"] = preservedApiBaseUrl,
                };

                ConfigFileHelper.WriteConfigFile(_paths.NetclawConfigPath, root);

                _acceptedBaseUrl = candidate.BaseUrl ?? string.Empty;
                HeartbeatIntervalDraft.Value = candidate.HeartbeatIntervalSeconds.ToString();
                IsSaved.Value = true;
                Status.Value = new ConfigStatusMessage(successMessage, ConfigStatusTone.Success);
                RequestRedraw();
                return true;
            },
            Status,
            "Registration save failed",
            RequestRedraw);
    }

    public void GoBack()
    {
        RouteRequested?.Invoke("/config");
        Navigate?.Invoke("/config");
    }

    public void RequestQuit()
    {
        ShutdownRequestedForTest = true;
        Shutdown();
    }

    public override void Dispose()
    {
        Enabled.Dispose();
        BaseUrlDraft.Dispose();
        HeartbeatIntervalDraft.Dispose();
        SelectedIndex.Dispose();
        Status.Dispose();
        IsSaved.Dispose();
        base.Dispose();
    }

    private void MarkDirty()
    {
        IsSaved.Value = false;
        ClearStatus();
        RequestRedraw();
    }

    private void ClearStatus()
    {
        if (!string.IsNullOrWhiteSpace(Status.Value.Text))
            Status.Value = new ConfigStatusMessage(string.Empty, ConfigStatusTone.Neutral);
    }

    private static (bool Enabled, string BaseUrl, string Interval) LoadState(NetclawPaths paths)
    {
        var root = ConfigFileHelper.LoadJsonDict(paths.NetclawConfigPath);
        var enabled = !ConfigFileHelper.TryGetPathValue(root, "Registration.Enabled", out var enabledValue)
            || enabledValue is not bool flag
            || flag;
        var baseUrl = ConfigFileHelper.TryGetPathValue(root, "Registration.BaseUrl", out var urlValue)
            && urlValue is string urlText
            && !string.IsNullOrWhiteSpace(urlText)
                ? urlText
                : string.Empty;
        var interval = ConfigFileHelper.TryGetPathValue(root, "Registration.HeartbeatIntervalSeconds", out var intervalValue)
            && intervalValue is long intervalNumber
                ? intervalNumber.ToString()
                : string.Empty;

        return (enabled, baseUrl, interval);
    }
}
