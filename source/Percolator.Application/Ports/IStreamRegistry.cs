using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Application2.Ports;

public enum StreamWriteStatus
{
    Success = 1,
    StreamClosed = 2,
    Backpressured = 3
}

public readonly record struct StreamWriteResult(
    StreamWriteStatus Status,
    string? ErrorMessage = null)
{
    public bool IsSuccess => Status == StreamWriteStatus.Success;

    public static StreamWriteResult Success() => new(StreamWriteStatus.Success);
    public static StreamWriteResult Closed(string? reason = null) => new(StreamWriteStatus.StreamClosed, reason);
    public static StreamWriteResult Backpressured(string? reason = null) => new(StreamWriteStatus.Backpressured, reason);
}

public interface IStreamRegistry
{
    bool HasActiveStream(PublicIdentityId targetId);
    bool HasActiveRelayStream(PublicIdentityId relayId);
    ValueTask<StreamWriteResult> TryWriteAsync(
        PublicIdentityId targetId,
        ReadOnlyMemory<byte> framedPayload,
        CancellationToken ct = default);
}
