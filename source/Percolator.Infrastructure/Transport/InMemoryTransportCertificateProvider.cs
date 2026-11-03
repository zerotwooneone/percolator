namespace Percolator.Infrastructure.Transport;

using System;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

public sealed class InMemoryTransportCertificateProvider : ITransportCertificateProvider
{
    private static readonly Oid ServerAuthenticationOid = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuthenticationOid = new("1.3.6.1.5.5.7.3.2");

    private readonly object _lock = new();
    private X509Certificate2? _certificate;
    private bool _disposed;

    public X509Certificate2 GetCertificate()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _certificate ??= CreateEphemeralCertificate();
        }
    }

    public ValueTask<X509Certificate2> GetOrCreateServerCertificateAsync(CancellationToken ct = default)
    {
        return ValueTask.FromResult(GetCertificate());
    }

    public ValueTask<X509Certificate2> RotateCertificateAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _certificate?.Dispose();
            _certificate = CreateEphemeralCertificate();
            return ValueTask.FromResult(_certificate);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _certificate?.Dispose();
            _certificate = null;
        }
    }

    private static X509Certificate2 CreateEphemeralCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest(new X500DistinguishedName("CN=PercolatorEphemeralPeer"), ecdsa, HashAlgorithmName.SHA256);

        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));

        var eku = new OidCollection
        {
            ServerAuthenticationOid,
            ClientAuthenticationOid
        };
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        sanBuilder.AddDnsName("localhost");
        req.CertificateExtensions.Add(sanBuilder.Build());

        return req.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddYears(1));
    }
}
