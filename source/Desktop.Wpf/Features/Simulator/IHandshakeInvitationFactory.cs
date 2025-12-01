using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Desktop.Wpf.Features.Simulator;

public interface IHandshakeInvitationFactory
{
    HandshakeInvitation CreateSynthetic(PeerId remotePeer, ProtocolVersion version, byte[]? payload = null);
}
