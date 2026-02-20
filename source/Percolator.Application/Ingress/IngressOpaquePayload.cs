using System;
using Percolator.Identity;

namespace Percolator.Application.Ingress;

public sealed record IngressOpaquePayload(
    byte[] PayloadBytes,
    SelfId SelfIdentityId,
    Guid? RemotePeerId = null,
    string? TransportPeer = null,
    string? CorrelationId = null);
