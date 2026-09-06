// -----------------------------------------------------------------------
// <copyright file="GovernanceServiceCollectionExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Actors.Protocol;

namespace Netclaw.Governance.SessionIntervention;

public static class GovernanceServiceCollectionExtensions
{
    /// <summary>
    /// Registers the always-on AGT runtime and the session intervention
    /// subsystem. Zero-config by design: the runtime needs no files and no
    /// endpoints, so an Enabled toggle would be a silent-bypass risk, not a
    /// feature.
    /// </summary>
    public static IServiceCollection AddAgentGovernance(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<AgentGovernanceRuntime>();
        services.AddSingleton<SessionInterventionStore>();
        services.AddSingleton<ISessionInterventionStateReader>(sp =>
            sp.GetRequiredService<SessionInterventionStore>());
        services.AddSingleton<SessionInterventionCoordinator>();
        return services;
    }
}
