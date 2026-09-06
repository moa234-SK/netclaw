// -----------------------------------------------------------------------
// <copyright file="DaemonPairingCodeProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Netclaw.Daemon.Security;
using Netclaw.Registration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Daemon-side adapter that mints registration auto-pairing codes from the
/// daemon's <see cref="PairingCodeService"/>. Keeps the registration library
/// free of a reference to the daemon executable. The minted code replaces any
/// pending code — including one from <c>netclaw daemon pair</c> — so the
/// manual command stays the override after boot.
/// </summary>
internal sealed class DaemonPairingCodeProvider(PairingCodeService pairingCodeService)
    : IRegistrationPairingProvider
{
    public string MintPairingCode() => pairingCodeService.GenerateCode().FormattedCode;
}
