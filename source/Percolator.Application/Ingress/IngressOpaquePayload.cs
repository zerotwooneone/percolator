using System;

namespace Percolator.Application.Ingress;

public sealed record IngressOpaquePayload(
    byte[] PayloadBytes,
    Guid? RemotePeerId = null,
    string? TransportPeer = null,
    string? CorrelationId = null);
