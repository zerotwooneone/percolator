namespace Percolator.Infrastructure.Tests.Transport.Primitives;

using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class InMemoryTransportCertificateProviderTests
{
    [Test]
    public async Task GetOrCreateServerCertificateAsync_ReturnsValidCertificateWithSan()
    {
        using var provider = new InMemoryTransportCertificateProvider();

        var cert = await provider.GetOrCreateServerCertificateAsync();

        cert.Should().NotBeNull();
        cert.HasPrivateKey.Should().BeTrue();
        cert.Subject.Should().Contain("CN=PercolatorEphemeralPeer");

        var sanExtension = cert.Extensions["2.5.29.17"] as X509SubjectAlternativeNameExtension;
        sanExtension.Should().NotBeNull();
        var dnsNames = sanExtension!.EnumerateDnsNames().ToList();
        dnsNames.Should().Contain("localhost");
    }

    [Test]
    public async Task RotateCertificateAsync_GeneratesNewCertificate()
    {
        using var provider = new InMemoryTransportCertificateProvider();

        var cert1 = await provider.GetOrCreateServerCertificateAsync();
        var thumbprint1 = cert1.Thumbprint;

        var cert2 = await provider.RotateCertificateAsync();
        var thumbprint2 = cert2.Thumbprint;

        thumbprint2.Should().NotBe(thumbprint1);
        cert2.HasPrivateKey.Should().BeTrue();
    }
}
