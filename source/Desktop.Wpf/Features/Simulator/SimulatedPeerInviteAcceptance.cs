using Percolator.Contracts;
using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Simulator;

public sealed record SimulatedPeerInviteAcceptance(
    SessionId SessionId,
    InviteHandshakeResponse Response);

