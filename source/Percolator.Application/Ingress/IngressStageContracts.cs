namespace Percolator.Application.Ingress;

public enum SessionResolutionPath
{
    FastPath = 0,
    SlowPath = 1
}

public sealed record SessionResolutionResult(
    Guid DirectSessionId,
    SessionResolutionPath Path);

public interface IIngressReadinessGate
{
    void EnsureReady();
}

public interface IIngressValidator
{
    void Validate(IngressOpaquePayload payload);
}

public interface IIngressSessionResolver
{
    SessionResolutionResult Resolve(IngressOpaquePayload payload);
}
