using System.Collections.Concurrent;
using System.Security.Cryptography;
using Google.Protobuf;

namespace Percolator.Grpc.Services;

public class PersistenceService
{
    public ConcurrentDictionary<ByteString,DateTimeOffset> PendingKeys { get; } = new();
    public ConcurrentBag<ByteString> BannedKeys { get; } = new();
    public ConcurrentDictionary<ByteString, SessionTracker> KnownSessionsByIdentity { get; } = new();
}

public class SessionTracker
{
    public KnownSession Current { get; private set; }
    private readonly List<ExpiringSession> ExpiringSessions = new();

    public SessionTracker(KnownSession current)
    {
        Current = current;
    }


    public void ReplaceCurrent(KnownSession newSession)
    {
        if (Current != null)
        {
            //todo: remove expired sessions
            ExpiringSessions.Add(new ExpiringSession
            {
                Session = Current,
                ExpirationTimeUtc = DateTimeOffset.UtcNow.AddMinutes(1)
            });
        }
        Current = newSession;
    }
}

public readonly struct ExpiringSession: IDisposable
{
    public KnownSession Session { get; init; }
    public DateTimeOffset ExpirationTimeUtc { get; init; }

    public void Dispose()
    {
        Session.Dispose();
    }
}
public record KnownSession: IDisposable
{
    public RSA EphemeralPublicKey { get; init; }
    public Aes SessionKey { get; init; }

    public void Dispose()
    {
        EphemeralPublicKey.Dispose();
        SessionKey.Dispose();
    }
}