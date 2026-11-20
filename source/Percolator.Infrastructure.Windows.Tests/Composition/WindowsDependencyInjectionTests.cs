namespace Percolator.Infrastructure.Windows.Tests.Composition;

using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Percolator.Apps.FileTransfer.Ports;
using Percolator.Infrastructure.Plugins.FileTransfer;
using Percolator.Infrastructure.Transport;
using Percolator.Infrastructure.Windows.DependencyInjection;
using Percolator.Infrastructure.Windows.Security;
using Percolator.Infrastructure.Windows.Storage;

[TestFixture]
[Platform(Include = "Win", Reason = "Validates Windows DI overrides on Windows platform.")]
public sealed class WindowsDependencyInjectionTests
{
    [Test]
    public void AddWindowsInfrastructureOverrides_SwapsFileStoreAndCertificateProvider()
    {
        var services = new ServiceCollection();

        // Register portable default adapters
        services.AddSingleton<ITransportCertificateProvider, InMemoryTransportCertificateProvider>();
        services.AddSingleton<IDesktopFileStorage, PortableDesktopFileStore>();

        // Apply Windows infrastructure overrides
        services.AddWindowsInfrastructureOverrides();

        var serviceProvider = services.BuildServiceProvider();

        // Assert resolution swaps to Windows implementations
        var certProvider = serviceProvider.GetRequiredService<ITransportCertificateProvider>();
        certProvider.Should().BeOfType<WindowsSChannelCertificateProvider>();

        var fileStore = serviceProvider.GetRequiredService<IDesktopFileStorage>();
        fileStore.Should().BeOfType<WindowsSparseDesktopFileStore>();

        // Assert descriptors were cleanly replaced without duplicates
        services.Count(s => s.ServiceType == typeof(ITransportCertificateProvider)).Should().Be(1);
        services.Count(s => s.ServiceType == typeof(IDesktopFileStorage)).Should().Be(1);
    }
}
