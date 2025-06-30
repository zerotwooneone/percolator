namespace Percolator.Application.Identity;

using System.Collections.Generic;
using System.Security.Cryptography.X509Certificates;

/// <summary>
/// Holds the details of the currently active identity for the running node.
/// This context is populated at startup and treated as read-only thereafter.
/// </summary>
public class ActiveIdentityContext
{
    public const string SigningKey = "signing";
    public const string IdentityKey = "identity";
    public const string PreKey = "prekey";

    public Percolator.Identity.PeerId? Id { get; internal set; }
    public string? IdentityName { get; internal set; }
    public string? Nickname { get; internal set; }
    public IReadOnlyDictionary<string, byte[]> PublicKeys { get; internal set; } = new Dictionary<string, byte[]>();
    public IReadOnlyDictionary<string, string> KeyThumbprints { get; internal set; } = new Dictionary<string, string>();
    public X509Certificate2? Certificate { get; internal set; }
    public Percolator.Identity.X3dhKeys? X3dhKeys { get; internal set; }
}
