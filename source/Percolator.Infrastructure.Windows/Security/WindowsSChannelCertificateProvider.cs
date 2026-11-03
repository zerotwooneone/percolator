namespace Percolator.Infrastructure.Windows.Security;

using System;
using System.IO;
using System.Net;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Infrastructure.Transport;

[SupportedOSPlatform("windows")]
public sealed class WindowsSChannelCertificateProvider : ITransportCertificateProvider
{
    private static readonly Oid ServerAuthenticationOid = new("1.3.6.1.5.5.7.3.1");
    private static readonly Oid ClientAuthenticationOid = new("1.3.6.1.5.5.7.3.2");

    private readonly string _tempDirectory;
    private readonly object _lock = new();
    private X509Certificate2? _certificate;
    private bool _disposed;

    public WindowsSChannelCertificateProvider(string? tempDirectory = null)
    {
        _tempDirectory = tempDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Percolator",
            "Temp");

        Directory.CreateDirectory(_tempDirectory);
        SweepOrphanedPfxFiles();
    }

    public ValueTask<X509Certificate2> GetOrCreateServerCertificateAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _certificate ??= CreateCngBackedCertificate();
            return ValueTask.FromResult(_certificate);
        }
    }

    public ValueTask<X509Certificate2> RotateCertificateAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CleanupCertificateUnderLock();
            _certificate = CreateCngBackedCertificate();
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
            CleanupCertificateUnderLock();
        }
    }

    private void CleanupCertificateUnderLock()
    {
        if (_certificate == null)
        {
            return;
        }

        try
        {
            if (_certificate.GetECDsaPrivateKey() is ECDsaCng cngKey)
            {
                try
                {
                    cngKey.Key.Delete();
                }
                catch
                {
                    // Best effort CNG key deletion
                }
            }
        }
        finally
        {
            _certificate.Dispose();
            _certificate = null;
        }
    }

    private X509Certificate2 CreateCngBackedCertificate()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var subjectName = new X500DistinguishedName("CN=Percolator Peer Node");
        var request = new CertificateRequest(subjectName, ecdsa, HashAlgorithmName.SHA256);

        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));

        var eku = new OidCollection
        {
            ServerAuthenticationOid,
            ClientAuthenticationOid
        };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(IPAddress.Loopback);
        sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(sanBuilder.Build());

        var notBefore = DateTimeOffset.UtcNow.AddHours(-1);
        var notAfter = DateTimeOffset.UtcNow.AddDays(30);

        using var ephemeralCert = request.CreateSelfSigned(notBefore, notAfter);

        var tempPfxPath = Path.Combine(_tempDirectory, $"ephemeral-{Guid.NewGuid():N}.pfx");
        var passwordBytes = new byte[32];
        RandomNumberGenerator.Fill(passwordBytes);
        var passwordChars = Convert.ToHexString(passwordBytes);

        try
        {
            var pfxBytes = ephemeralCert.Export(X509ContentType.Pfx, passwordChars);
            try
            {
                File.WriteAllBytes(tempPfxPath, pfxBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pfxBytes);
            }

            return LoadPfxWithRetry(tempPfxPath, passwordChars);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);

            if (File.Exists(tempPfxPath))
            {
                try
                {
                    File.Delete(tempPfxPath);
                }
                catch
                {
                    // Best effort cleanup; sweep will handle orphaned files on next startup
                }
            }
        }
    }

    private static X509Certificate2 LoadPfxWithRetry(string pfxPath, string password)
    {
        const int maxAttempts = 3;
        var delays = new[] { 50, 150, 350 };

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(
                    pfxPath,
                    password,
                    X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
            }
            catch (IOException) when (attempt < maxAttempts - 1)
            {
                Thread.Sleep(delays[attempt]);
            }
        }

        return X509CertificateLoader.LoadPkcs12FromFile(
            pfxPath,
            password,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }

    private void SweepOrphanedPfxFiles()
    {
        try
        {
            if (!Directory.Exists(_tempDirectory))
            {
                return;
            }

            var pfxFiles = Directory.GetFiles(_tempDirectory, "ephemeral-*.pfx");
            foreach (var file in pfxFiles)
            {
                try
                {
                    File.Delete(file);
                }
                catch
                {
                    // Best effort file deletion for files in active use
                }
            }
        }
        catch
        {
            // Best effort directory sweep
        }
    }
}
