namespace Desktop.Wpf.Features.Sessions.Models;

public enum SecureChannelKeyType
{
    SecureSession,
    PendingCorrelation,
    PendingSession
}

public readonly record struct SecureChannelKey(SecureChannelKeyType Type, Guid Value)
{
    public static SecureChannelKey FromSessionId(Guid sessionId)
        => new(SecureChannelKeyType.SecureSession, sessionId);

    public static SecureChannelKey FromPendingCorrelationId(Guid correlationId)
        => new(SecureChannelKeyType.PendingCorrelation, correlationId);

    public static SecureChannelKey FromPendingSessionId(Guid pendingSessionId)
        => new(SecureChannelKeyType.PendingSession, pendingSessionId);

    public override string ToString()
        => $"{Type}:{Value:N}";
}
