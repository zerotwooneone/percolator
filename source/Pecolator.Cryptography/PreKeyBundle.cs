using System.Security.Cryptography;

namespace Pecolator.Cryptography;

public record PreKeyBundle(byte[] IdentityKey, byte[] SignedPreKey, byte[] Signature, byte[] OneTimePreKey);
