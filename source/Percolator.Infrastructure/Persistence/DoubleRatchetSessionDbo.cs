using System;
using System.Collections.Generic;

namespace Percolator.Infrastructure.Persistence;

public class DoubleRatchetSessionDbo
{
    public Guid SessionId { get; set; }

    // Partitioning scope
    public int SelfIdentityId { get; set; }

    // Core state
    public byte[] RootKey { get; set; } = Array.Empty<byte>();
    public bool RatchetFlag { get; set; }

    public byte[]? SendingChainKey { get; set; }
    public byte[]? ReceivingChainKey { get; set; }

    public ulong SendingCounter { get; set; }
    public ulong ReceivingCounter { get; set; }
    public ulong PreviousChainLength { get; set; }

    // Keys
    public byte[]? TheirDhRatchetPublicKey { get; set; }
    public byte[]? DhRatchetPrivateKey { get; set; }
    public byte[] TheirIdentityPublicKey { get; set; } = Array.Empty<byte>();

    // Aux
    public DateTimeOffset UpdatedAt { get; set; }

    public List<SkippedMessageKeyDbo> SkippedMessageKeys { get; set; } = new();
}
