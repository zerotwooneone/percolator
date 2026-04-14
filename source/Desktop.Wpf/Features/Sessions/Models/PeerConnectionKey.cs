namespace Desktop.Wpf.Features.Sessions.Models;

public enum SecureChannelKeyType
{
    SecureSession,
    PendingCorrelation,
    PendingSession
}

public readonly record struct PeerConnectionKey(SecureChannelKeyType Type, Guid Value)
{
    public static PeerConnectionKey FromSessionId(Guid sessionId)
        => new(SecureChannelKeyType.SecureSession, sessionId);

    public static PeerConnectionKey FromPendingCorrelationId(Guid correlationId)
        => new(SecureChannelKeyType.PendingCorrelation, correlationId);

    public static PeerConnectionKey FromPendingSessionId(Guid pendingSessionId)
        => new(SecureChannelKeyType.PendingSession, pendingSessionId);

    public override string ToString()
        => $"{Type}:{Value:N}";
}
