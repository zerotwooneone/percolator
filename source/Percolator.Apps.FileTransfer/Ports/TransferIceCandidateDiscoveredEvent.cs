using System;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.PluginSdk.Events;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Apps.FileTransfer.Ports;

public sealed record TransferIceCandidateDiscoveredEvent(
    Guid EventId,
    DateTimeOffset OccurredOnUtc,
    TransferSessionId SessionId,
    PublicIdentityId RemotePeerId,
    string Candidate,
    string? CandidateMid,
    CertSha256Fingerprint? LocalDtlsFingerprint) : IApplicationEvent;
