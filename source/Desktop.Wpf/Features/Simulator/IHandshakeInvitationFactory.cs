using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Simulator;

public interface IHandshakeInvitationFactory
{
    HandshakeInvitation CreateSynthetic(CryptoPeerId remoteCryptoPeer, ProtocolVersion version, byte[]? payload = null);
}
