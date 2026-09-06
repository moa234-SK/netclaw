// -----------------------------------------------------------------------
// <copyright file="RegistrationConfigPage.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using R3;
using Termina.Extensions;
using Termina.Input;
using Termina.Layout;
using Termina.Reactive;
using Termina.Rendering;
using Termina.Terminal;

namespace Netclaw.Cli.Tui.Config;

internal sealed class RegistrationConfigPage : ReactivePage<RegistrationConfigViewModel>
{
    private DynamicLayoutNode? _contentNode;
    // PasteEvent carries its text privately; a scratch TextInputNode is the
    // established bridge (see InboundWebhooksConfigPage) to read it back.
    private readonly TextInputNode _pasteBuffer = new();

    protected override void OnBound()
    {
        base.OnBound();
        ViewModel.Input.OfType<IInputEvent, KeyPressed>()
            .Subscribe(HandleKeyPress)
            .DisposeWith(Subscriptions);
        ViewModel.Input.OfType<IInputEvent, PasteEvent>()
            .Subscribe(HandlePaste)
            .DisposeWith(Subscriptions);

        ViewModel.Enabled.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.BaseUrlDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.HeartbeatIntervalDraft.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.SelectedIndex.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
        ViewModel.IsSaved.Subscribe(_ => _contentNode?.Invalidate()).DisposeWith(Subscriptions);
    }

    public override ILayoutNode BuildLayout()
        => NetclawTuiChrome.BuildPageFrame("Registration", BuildInnerLayout());

    private ILayoutNode BuildInnerLayout()
        => Layouts.Vertical()
            .WithSpacing(1)
            .WithChild(BuildContent())
            .WithChild(Layouts.Empty().Fill())
            .WithChild(BuildStatusBar())
            .WithChild(NetclawTuiChrome.BuildKeyHintLine(
                " [↑/↓] Navigate  [Enter] Toggle/Save  [Esc] Back  [Ctrl+Q] Quit"));

    private LayoutNode BuildContent()
    {
        _contentNode = new DynamicLayoutNode(() =>
        {
            var baseUrl = ViewModel.BaseUrlDraft.Value;
            var interval = ViewModel.HeartbeatIntervalDraft.Value;

            var layout = Layouts.Vertical()
                .WithChild(Row(RegistrationConfigViewModel.EnabledRow,
                    "Enabled",
                    ViewModel.Enabled.Value ? "on" : "off"))
                .WithChild(Row(RegistrationConfigViewModel.BaseUrlRow,
                    "Panel base URL",
                    string.IsNullOrWhiteSpace(baseUrl) ? "– not set" : baseUrl))
                .WithChild(Row(RegistrationConfigViewModel.IntervalRow,
                    "Heartbeat seconds",
                    string.IsNullOrWhiteSpace(interval) ? "300 (default)" : interval))
                .WithChild(Layouts.Empty().Height(1))
                .WithChild(new TextNode(
                        "  The daemon derives two endpoints from the base URL:")
                    .WithForeground(Color.Gray))
                .WithChild(new TextNode(
                        "  POST {base}/api/v1/agent/status   full status at startup and on health changes")
                    .WithForeground(Color.Gray))
                .WithChild(new TextNode(
                        "  POST {base}/api/v1/agent/heartbeat   liveness between changes")
                    .WithForeground(Color.Gray))
                .WithChild(new TextNode(
                        "  A blank URL disables registration. The base must be absolute HTTP or")
                    .WithForeground(Color.Gray))
                .WithChild(new TextNode(
                        "  HTTPS. The interval must be between 30 and 86400 seconds.")
                    .WithForeground(Color.Gray));

            return layout;
        });

        return _contentNode;
    }

    private ILayoutNode Row(int index, string label, string value)
    {
        var focused = index == ViewModel.SelectedIndex.Value;
        return ConfigSelectionRow.Create($"  {label,-20} {value}", focused);
    }

    private LayoutNode BuildStatusBar()
        => ViewModel.Status
            .Select(status => (ILayoutNode)(string.IsNullOrWhiteSpace(status.Text)
                ? Layouts.Empty()
                : NetclawTuiChrome.BuildStatusLine(status.Text, ToColor(status.Tone))))
            .AsLayout()
            .Height(1);

    private void HandleKeyPress(KeyPressed key)
    {
        var keyInfo = key.KeyInfo;
        if (keyInfo.Key == ConsoleKey.Q && keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control))
        {
            ViewModel.RequestQuit();
            return;
        }

        switch (keyInfo.Key)
        {
            case ConsoleKey.Escape:
                ViewModel.GoBack();
                return;
            case ConsoleKey.UpArrow:
                ViewModel.MoveSelection(-1);
                ViewModel.RequestRedraw();
                return;
            case ConsoleKey.DownArrow:
                ViewModel.MoveSelection(1);
                ViewModel.RequestRedraw();
                return;
            case ConsoleKey.Enter:
                ViewModel.ActivateSelected();
                ViewModel.RequestRedraw();
                return;
            case ConsoleKey.Backspace:
                ViewModel.Backspace();
                ViewModel.RequestRedraw();
                return;
            default:
                if (!char.IsControl(keyInfo.KeyChar))
                {
                    ViewModel.AppendText(keyInfo.KeyChar.ToString());
                    ViewModel.RequestRedraw();
                }

                return;
        }
    }

    private void HandlePaste(PasteEvent paste)
    {
        _pasteBuffer.Text = string.Empty;
        _pasteBuffer.HandlePaste(paste);
        ViewModel.AppendText(_pasteBuffer.Text);
        ViewModel.RequestRedraw();
    }

    private static Color ToColor(ConfigStatusTone tone)
        => tone switch
        {
            ConfigStatusTone.Success => Color.Green,
            ConfigStatusTone.Warning => Color.Yellow,
            ConfigStatusTone.Error => Color.Red,
            _ => Color.Gray
        };
}
