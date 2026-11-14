namespace Percolator.Application.Ports;

using Percolator.Domain.Security.Ports;

/// <summary>
/// Executable-owned composition root for the Stage-2 session host. The concrete
/// implementation lives in the executable (e.g. Desktop) so post-unlock services and
/// ViewModels get real constructor injection from the session container — the session
/// boundary is expressed as which container resolved you, not a threaded parameter.
/// </summary>
public interface ISessionHostFactory
{
    /// <summary>
    /// Builds the Stage-2 host for <paramref name="masterKey"/> and returns the session
    /// wrapping it. The session owns the host lifetime; call sites start it via
    /// <see cref="IAuthenticatedSession.StartAsync"/>.
    /// </summary>
    IAuthenticatedSession CreateSession(IMasterKeyHandle masterKey);
}
