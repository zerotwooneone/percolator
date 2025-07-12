using System.Security.Cryptography.X509Certificates;

namespace Percolator.Application.Network;

public interface IPeerTrustManager
{
    void Initialize();
    bool IsTrusted(X509Certificate2 presentedCertificate);
    Task AddTrustedPeer(X509Certificate2 certificate);
}
