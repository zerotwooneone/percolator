namespace Percolator.Infrastructure.Tests.Transport.Primitives;

using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class TransportCertificateSanBuilderTests
{
    [Test]
    public void PopulateSan_NullBuilder_ThrowsArgumentNullException()
    {
        Action act = () => TransportCertificateSanBuilder.PopulateSan(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Test]
    public void PopulateSan_PopulatesGuaranteedInvariants_LoopbackAndLocalhost()
    {
        var builder = new SubjectAlternativeNameBuilder();
        TransportCertificateSanBuilder.PopulateSan(builder);
        var raw = builder.Build();
        var extension = new X509SubjectAlternativeNameExtension(raw.RawData, false);

        var dnsNames = extension.EnumerateDnsNames().ToList();
        var ips = extension.EnumerateIPAddresses().ToList();

        dnsNames.Should().Contain("localhost");
        var hostName = Dns.GetHostName();
        if (!string.IsNullOrWhiteSpace(hostName) && Uri.CheckHostName(hostName) == UriHostNameType.Dns)
        {
            dnsNames.Should().Contain(hostName);
        }

        ips.Should().Contain(IPAddress.Loopback);
        ips.Should().Contain(IPAddress.IPv6Loopback);

        foreach (var ip in ips)
        {
            ip.AddressFamily.Should().Match(af => af == AddressFamily.InterNetwork || af == AddressFamily.InterNetworkV6);
            if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            {
                ip.ScopeId.Should().Be(0, "X.509 SAN IP addresses must have normalized ScopeId");
            }
        }
    }

    [Test]
    public void PopulateSan_DoesNotDuplicateAddressesOrDnsNames()
    {
        var builder = new SubjectAlternativeNameBuilder();
        TransportCertificateSanBuilder.PopulateSan(builder);
        var raw = builder.Build();
        var extension = new X509SubjectAlternativeNameExtension(raw.RawData, false);

        var dnsNames = extension.EnumerateDnsNames().ToList();
        var ips = extension.EnumerateIPAddresses().ToList();

        dnsNames.Should().OnlyHaveUniqueItems();
        ips.Should().OnlyHaveUniqueItems();
    }
}
