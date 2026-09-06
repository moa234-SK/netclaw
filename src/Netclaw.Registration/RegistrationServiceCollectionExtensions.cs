// -----------------------------------------------------------------------
// <copyright file="RegistrationServiceCollectionExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Netclaw.Configuration;
using Netclaw.Configuration.Http;

namespace Netclaw.Registration;

public static class RegistrationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the self-registration hosted service. Call only when the
    /// config is enabled and a URL is set — the caller owns the off-state
    /// gate, so the service is never constructed in the off state. An
    /// invalid URL throws here, which fails daemon startup loudly.
    /// </summary>
    public static IServiceCollection AddSelfRegistration(
        this IServiceCollection services,
        RegistrationConfig config,
        NetclawPaths paths,
        Func<IServiceProvider, IStatusSnapshotProvider> statusProviderFactory)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(statusProviderFactory);

        RegistrationConfigValidator.ValidateOrThrow(config);

        services.AddSingleton(config);
        services.AddSingleton(new RegistrationStateStore(paths));
        services.AddSingleton(statusProviderFactory);
        services.AddHttpClient<SelfRegistrationService>(client =>
                client.Timeout = SelfRegistrationService.SendTimeout)
            .AddNetclawHeaders("self-registration");
        services.AddHostedService<SelfRegistrationService>();
        return services;
    }
}
