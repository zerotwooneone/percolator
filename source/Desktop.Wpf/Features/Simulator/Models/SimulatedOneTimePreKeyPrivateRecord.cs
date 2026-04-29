using Percolator.Cryptography;

namespace Desktop.Wpf.Features.Simulator.Models;

public sealed record SimulatedOneTimePreKeyPrivateRecord(
    SimulatedOneTimePreKeyId Id,
    PrivatePreKey PrivateKey,
    DateTimeOffset CreatedAtUtc);
