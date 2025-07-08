using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Percolator.Infrastructure.Chat;
using Percolator.Infrastructure.Identity;
using Percolator.Infrastructure.Sessions;
using System.IO;

namespace Percolator.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructureServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SectionName))
            .Configure(options =>
            {
                if (!Path.IsPathRooted(options.Path))
                {
                    var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    options.Path = Path.Combine(appDataPath, options.Path);
                }
            });
        services.AddChatInfrastructure();
        services.AddSessionsInfrastructure();
        services.AddIdentityInfrastructure();

        return services;
    }
}
