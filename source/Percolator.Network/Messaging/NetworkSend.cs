namespace Percolator.Network.Messaging;

// Value object wrapping bytes for network delivery (opaque to the Network domain consumers)
public readonly record struct NetworkPayload(ReadOnlyMemory<byte> Value)
{
    public static NetworkPayload FromArray(byte[] bytes) => new(bytes);
}

// Result type for transport operations, including endpoint attribution for direct sends
public sealed record TransportSendResult(
    bool Ok,
    NetworkPayload? ResponsePayload,
    SendFailureReason? Reason,
    Exception? Error,
    System.Net.DnsEndPoint? UsedEndpoint);

public enum SendStrategy
{
    DirectOnly = 0,
    DirectThenRelay = 1
}

public enum SendFailureReason
{
    None = 0,
    NoPeerConnection = 1,
    NoEndpoints = 2,
    NoRelayConfigured = 3,
    NoRelaySession = 4,
    TransportUnavailable = 5,
    Timeout = 6,
    TlsValidationFailed = 7,
    Unknown = 255
}

public sealed class AttemptDetail
{
    public string Route { get; init; } = string.Empty; // e.g., "Direct", "Relay:HOST"
    public string? Endpoint { get; init; }
    public TimeSpan Duration { get; init; }
    public SendFailureReason? Reason { get; init; }
}

public sealed class SendOutcome
{
    public bool Success { get; init; }
    public string Path { get; init; } = "None"; // "Direct" or "Relay:HOST"
    public IReadOnlyList<string> AttemptedPaths { get; init; } = Array.Empty<string>();
    public int Attempts { get; init; }
    public SendFailureReason? Reason { get; init; }
    public Exception? LastError { get; init; }
    public IReadOnlyList<AttemptDetail> AttemptsDetail { get; init; } = Array.Empty<AttemptDetail>();
    public NetworkPayload? ResponsePayload { get; init; }
}

public interface INetworkSender
{
    Task<SendOutcome> SendAsync(uint selfIdentityId, PeerId target, NetworkPayload payload, SendStrategy strategy, CancellationToken ct = default);
}

// Route planning and relay topology abstractions (to be implemented by Application or Infrastructure layers)
public interface IRelayTopology
{
    Task<PeerId?> GetRelayForAsync(PeerId target, CancellationToken ct = default);
}

public interface ISendExecutor
{
    Task<SendOutcome> ExecuteAsync(uint selfIdentityId, PeerId target, NetworkPayload payload, IReadOnlyList<PlannedRoute> plannedRoutes, CancellationToken ct = default);
}
