namespace Percolator.Domain.Identities.ValueObjects;

/// <summary>
/// Identifies a signed prekey for a device. Relay and local stores retain multiple SPKs so
/// in-flight handshakes keep decrypting across rotation; <c>0</c> is invalid for a found bundle.
/// </summary>
public readonly record struct SignedPreKeyId(uint Value) : IEquatable<SignedPreKeyId>
{
    public static SignedPreKeyId First => new(1);

    public bool IsValid => Value > 0;

    public override string ToString() => Value.ToString();
}
