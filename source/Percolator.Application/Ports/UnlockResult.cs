namespace Percolator.Application2.Ports;

/// <summary>
/// A polymorphic discriminated union representing the outcome of a vault unlock attempt.
/// </summary>
public abstract record UnlockResult
{
    private UnlockResult() { }

    /// <summary>
    /// Unlock succeeded with the valid master passphrase. Contains the active authenticated session.
    /// </summary>
    /// <param name="Session">The active authenticated Stage 2 session.</param>
    public sealed record Success(IAuthenticatedSession Session) : UnlockResult;

    /// <summary>
    /// The emergency duress passphrase was matched. Cryptographic shredding was executed and the
    /// app transitions to a decoy state. <paramref name="Session"/> is the authenticated session
    /// for the freshly-created decoy vault (keyed by the duress passphrase) when one was built —
    /// personas created during the session must persist into the decoy vault so subsequent duress
    /// logins look authentic. Null when the decoy vault could not be initialized; the UI then
    /// falls back to the in-memory decoy path.
    /// </summary>
    public sealed record DuressActivated(IAuthenticatedSession? Session = null) : UnlockResult;

    /// <summary>
    /// The candidate passphrase did not match either the master slot or duress slot.
    /// </summary>
    public sealed record InvalidCredentials : UnlockResult;
}
