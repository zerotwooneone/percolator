namespace Percolator.Application.Ports;

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
    /// for the freshly-created decoy vault (keyed by the duress passphrase) — personas created
    /// during the session persist into the decoy vault so subsequent duress logins look
    /// authentic. There is no sessionless variant: decoy-vault initialization failure
    /// propagates as an unlock failure rather than degrading to a no-vault decoy.
    /// </summary>
    public sealed record DuressActivated(IAuthenticatedSession Session) : UnlockResult;

    /// <summary>
    /// The candidate passphrase did not match either the master slot or duress slot.
    /// </summary>
    public sealed record InvalidCredentials : UnlockResult;
}
