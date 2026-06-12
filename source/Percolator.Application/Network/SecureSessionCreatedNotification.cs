using MediatR;
using Percolator.Cryptography;
using CryptoPeerId = Percolator.Cryptography.Primitives.PeerId;

namespace Percolator.Application.Network;

public sealed record SecureSessionCreatedNotification(
    SessionId SessionId,
    SecureSessionCreatedReason Reason,
    CryptoPeerId? RemotePeerId = null,
    ProtocolVersion? ProtocolVersion = null) : INotification;
