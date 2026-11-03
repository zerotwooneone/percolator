namespace Percolator.Infrastructure.Windows.DependencyInjection;

using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Windows.Security;

[SupportedOSPlatform("windows")]
public static class WindowsInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsInfrastructureOverrides(this IServiceCollection services)
    {
        services.AddSingleton<ITransportCertificateProvider, WindowsSChannelCertificateProvider>();
        return services;
    }
}
