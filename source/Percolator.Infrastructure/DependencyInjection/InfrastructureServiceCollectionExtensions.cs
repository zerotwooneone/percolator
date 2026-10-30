namespace Percolator.Infrastructure.DependencyInjection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Application2.Ports;
using Percolator.Domain.Common;
using Percolator.Infrastructure.Common;
using Percolator.Infrastructure.Vault;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddBootstrapInfrastructure(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        SQLitePCL.Batteries_V2.Init();

        services.AddSingleton<VaultStorageOptions>();
        services.AddSingleton<IDateTimeProvider, SystemDateTimeProvider>();
        services.AddSingleton<IHeaderStorage, ProtobufHeaderStorage>();
        services.AddSingleton<IDuressWipeHandler, DuressWipeHandler>();
        services.AddSingleton<IVaultEnvelopePacker, VaultEnvelopePacker>();
        services.AddSingleton<IDatabaseCanaryProbe, DatabaseCanaryProbe>();
        services.AddSingleton<IDatabaseMigrator, NoOpDatabaseMigrator>();
        services.AddSingleton<IStage2HostFactory, DefaultStage2HostFactory>();
        services.AddSingleton<IDatabaseUnlockCoordinator, SqlCipherDatabaseUnlockCoordinator>();

        return services;
    }
}
