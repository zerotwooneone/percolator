namespace Percolator.Infrastructure.Windows.DependencyInjection;

using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Windows.Security;
using Percolator.Infrastructure.Windows.Storage;

[SupportedOSPlatform("windows")]
public static class WindowsInfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddWindowsInfrastructureOverrides(this IServiceCollection services)
    {
        services.Replace(ServiceDescriptor.Singleton<ITransportCertificateProvider, WindowsSChannelCertificateProvider>());
        services.Replace(ServiceDescriptor.Singleton<IDesktopFileStorage, WindowsSparseDesktopFileStore>());
        return services;
    }
}
