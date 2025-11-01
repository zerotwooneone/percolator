using MediatR;
using System.Security.Cryptography.X509Certificates;
using System.Net;

namespace Percolator.Application.Network
{
    public sealed class EstablishDirectSessionCommand : IRequest<EstablishDirectSessionResult>
    {
        public required byte[] IdentitySigningKeyBytes { get; init; }
        public required byte[] SignedPayloadBytes { get; init; }
        public required byte[] PayloadSignatureBytes { get; init; }
        public byte[]? OneTimePreKeyBytes { get; init; }
        public required byte[] PreKeyBytes { get; init; }
        public X509Certificate2? ClientCertificate { get; init; }
        public required DnsEndPoint PeerEndPoint { get; init; }
    }

    public sealed class EstablishDirectSessionResult
    {
        public required string SessionId { get; init; }
        public required byte[] ResponsePayloadBytes { get; init; }
        public required byte[] IdentitySigningKeyBytes { get; init; }
        public required byte[] RemoteEphemeralKeyBytes { get; init; }
        public required byte[] RatchetMessageBytes { get; init; }
    }
}
