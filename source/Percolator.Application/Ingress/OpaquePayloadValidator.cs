using System;

namespace Percolator.Application.Ingress;

public sealed class OpaquePayloadValidator : IIngressValidator
{
    private const int MaxBytes = 1024 * 1024; // 1 MiB hard cap

    public void Validate(IngressOpaquePayload payload)
    {
        if (payload is null) throw new ArgumentNullException(nameof(payload));
        if (payload.PayloadBytes is null || payload.PayloadBytes.Length == 0) throw new InvalidOperationException("payload bytes required");
        if (payload.PayloadBytes.Length > MaxBytes) throw new InvalidOperationException("payload too large");
    }
}
