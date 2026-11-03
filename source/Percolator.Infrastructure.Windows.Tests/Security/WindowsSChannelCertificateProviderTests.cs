namespace Percolator.Infrastructure.Windows.Tests.Security;

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Infrastructure.Windows.Security;

[TestFixture]
public sealed class WindowsSChannelCertificateProviderTests
{
    private string _testTempDir = null!;

    [SetUp]
    public void SetUp()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), "PercolatorTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testTempDir);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_testTempDir))
        {
            try
            {
                Directory.Delete(_testTempDir, recursive: true);
            }
            catch
            {
                // Best effort test cleanup
            }
        }
    }

    [Test]
    public async Task GetOrCreateServerCertificateAsync_ReturnsValidCngBackedCert_AndDeletesTempPfx()
    {
        using var provider = new WindowsSChannelCertificateProvider(_testTempDir);

        var cert = await provider.GetOrCreateServerCertificateAsync();

        cert.Should().NotBeNull();
        cert.HasPrivateKey.Should().BeTrue();
        cert.Subject.Should().Contain("CN=Percolator Peer Node");

        var sanExtension = cert.Extensions["2.5.29.17"] as X509SubjectAlternativeNameExtension;
        sanExtension.Should().NotBeNull();
        var dnsNames = sanExtension!.EnumerateDnsNames().ToList();
        dnsNames.Should().Contain("localhost");

        var pfxFiles = Directory.GetFiles(_testTempDir, "*.pfx");
        pfxFiles.Should().BeEmpty();
    }

    [Test]
    public async Task RotateCertificateAsync_GeneratesNewCertificate()
    {
        using var provider = new WindowsSChannelCertificateProvider(_testTempDir);

        var cert1 = await provider.GetOrCreateServerCertificateAsync();
        var thumbprint1 = cert1.Thumbprint;

        var cert2 = await provider.RotateCertificateAsync();
        var thumbprint2 = cert2.Thumbprint;

        thumbprint2.Should().NotBe(thumbprint1);
        cert2.HasPrivateKey.Should().BeTrue();

        var pfxFiles = Directory.GetFiles(_testTempDir, "*.pfx");
        pfxFiles.Should().BeEmpty();
    }

    [Test]
    public void SweepOrphanedPfxFiles_CleansStaleFilesOnStartup()
    {
        var stalePfx = Path.Combine(_testTempDir, "ephemeral-stale.pfx");
        File.WriteAllBytes(stalePfx, [0x01, 0x02, 0x03]);

        using var provider = new WindowsSChannelCertificateProvider(_testTempDir);

        File.Exists(stalePfx).Should().BeFalse();
    }
}
