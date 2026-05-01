using System.Diagnostics;

namespace Percolator.Cryptography;

/// <summary>
/// A serialization-friendly identifier for skipped message keys.
/// Used to uniquely identify message keys by their ratchet ephemeral key and message number.
/// </summary>
[DebuggerDisplay("{RatchetKey}:{MessageNumber}")]
public class SkippedMessageKeyIdentifier : IEquatable<SkippedMessageKeyIdentifier>
{
    /// <summary>
    /// The ephemeral ratchet key associated with the message.
    /// </summary>
    public RatchetEphemeralKey RatchetKey { get; }
    
    /// <summary>
    /// The message number.
    /// </summary>
    public ulong MessageNumber { get; }
    
    /// <summary>
    /// Creates a new skipped message key identifier.
    /// </summary>
    public SkippedMessageKeyIdentifier(RatchetEphemeralKey ratchetKey, ulong messageNumber)
    {
        RatchetKey = ratchetKey ?? throw new ArgumentNullException(nameof(ratchetKey));
        MessageNumber = messageNumber;
    }
    
    /// <summary>
    /// Returns a string representation of this identifier.
    /// </summary>
    public override string ToString() => $"{Convert.ToBase64String(RatchetKey.ToArray())}:{MessageNumber}";

    /// <summary>
    /// Determines if this identifier equals another object.
    /// </summary>
    public override bool Equals(object? obj)
    {
        if (obj is SkippedMessageKeyIdentifier other)
        {
            return Equals(other);
        }
        return false;
    }

    /// <summary>
    /// Determines if this identifier equals another skipped message key identifier.
    /// </summary>
    public bool Equals(SkippedMessageKeyIdentifier? other)
    {
        if (other is null)
            return false;
            
        return MessageNumber == other.MessageNumber && 
               ((RatchetKey is null && other.RatchetKey is null) ||
                (RatchetKey is not null && other.RatchetKey is not null && 
                 RatchetKey.ToArray().AsSpan().SequenceEqual(other.RatchetKey.ToArray())));
    }

    /// <summary>
    /// Returns a hash code for this identifier.
    /// </summary>
    public override int GetHashCode()
    {
        var hashCode = new HashCode();
        
        if (RatchetKey?.ToArray() is not null)
        {
            foreach (var b in RatchetKey.ToArray())
            {
                hashCode.Add(b);
            }
        }
        
        hashCode.Add(MessageNumber);
        return hashCode.ToHashCode();
    }
}
