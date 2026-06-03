using Microsoft.Extensions.DependencyInjection;
using Percolator.Application.Cryptography;
using Percolator.Cryptography;
using Percolator.Chat.App;
using Percolator.Infrastructure.Application;

namespace Percolator.Infrastructure.Cryptography;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCryptographyInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IPreKeyBundleRepository, SqlitePreKeyBundleRepository>();
        services.AddScoped<IAdminSignatureVerifier, AdminSignatureVerifier>();
        services.AddScoped<IPendingSessionRepository, SqlitePendingSessionRepository>();
        services.AddScoped<ISentInvitationRepository, SqliteSentInvitationRepository>();
        services.AddScoped<ISessionRepository, SqliteSessionRepository>();
        services.AddScoped<IPendingHandshakeQueries, PendingHandshakeQueries>();
        services.AddScoped<IPendingSessionQueries, PendingSessionQueries>();
        services.AddSingleton<IX3dhDeriver, X3dhDeriver>();
        services.AddSingleton<IPreKeyBundleValidator, PreKeyBundleValidator>();
        services.AddSingleton<IRatchetEngine, AeadRatchetEngine>();
        services.AddSingleton<IGroupCryptographyService, ZkgroupCryptographyService>();
        return services;
    }
}
