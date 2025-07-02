using Percolator.Identity;
using Percolator.Identity.Model;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Percolator.ApplicationTests")]

namespace Percolator.Application.Identity;

/// <summary>
/// Holds the details of the currently active identity for the running node.
/// This context is populated at startup and treated as read-only thereafter.
/// </summary>
public class ActiveIdentityContext
{
    public const string SigningKey = "signing";
    public const string IdentityKey = "identity";
    public const string PreKey = "prekey";

    public IdentityRecord? Identity { get; internal set; }
    public X3dhKeys? Keys { get; internal set; }
}
