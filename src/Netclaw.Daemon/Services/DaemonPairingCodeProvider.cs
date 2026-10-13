// -----------------------------------------------------------------------
// <copyright file="DaemonPairingCodeProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Akka.Actor;
using Akka.Hosting;
using Netclaw.Configuration;
using Netclaw.Daemon.Security;
using Netclaw.Registration;

namespace Netclaw.Daemon.Services;

/// <summary>
/// Daemon-side adapter that mints registration auto-pairing codes by asking
/// the daemon's <see cref="PairingActor"/>. Keeps the registration library
/// free of a reference to the daemon executable. The minted code replaces any
/// pending code — including one from <c>netclaw daemon pair</c> — so the
/// manual command stays the override after boot.
/// </summary>
internal sealed class DaemonPairingCodeProvider(IRequiredActor<PairingActor> pairingActor)
    : IRegistrationPairingProvider
{
    public async Task<string> MintPairingCodeAsync(CancellationToken cancellationToken)
    {
        var result = await pairingActor.ActorRef.Ask<PairingCodeResultDto>(
            new PairingActor.GenerateCode(cancellationToken),
            cancellationToken);
        return result.FormattedCode;
    }
}
