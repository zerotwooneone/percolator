using System.Security.Cryptography;
using Percolator.Domain.Common;

namespace Percolator.Apps.Discovery.Features.PrivateRendezvous;

/// <summary>
/// Strong value object representing a high-entropy 32-byte shared secret used for rendezvous derivation and AEAD.
/// </summary>
public readonly record struct RendezvousSharedSecret
{
    public const int RequiredByteLength = 32;
    private readonly byte[]? _bytes;

    public bool IsValid => _bytes is { Length: RequiredByteLength };
    public ReadOnlySpan<byte> Span => _bytes ?? ReadOnlySpan<byte>.Empty;
    public ReadOnlyMemory<byte> Memory => _bytes ?? ReadOnlyMemory<byte>.Empty;

    public RendezvousSharedSecret(ReadOnlySpan<byte> secretBytes)
    {
        if (secretBytes.Length != RequiredByteLength)
        {
            throw new ArgumentException($"Rendezvous shared secret must be exactly {RequiredByteLength} bytes.", nameof(secretBytes));
        }
        _bytes = secretBytes.ToArray();
    }

    public static DomainResult<RendezvousSharedSecret> Create(ReadOnlySpan<byte> secretBytes)
    {
        if (secretBytes.Length != RequiredByteLength)
        {
            return DomainResult<RendezvousSharedSecret>.Failure(new DomainError(
                "INVALID_SECRET_LENGTH", $"Rendezvous shared secret must be exactly {RequiredByteLength} bytes."));
        }
        return DomainResult<RendezvousSharedSecret>.Success(new RendezvousSharedSecret(secretBytes));
    }

    public static RendezvousSharedSecret Generate()
    {
        var bytes = new byte[RequiredByteLength];
        RandomNumberGenerator.Fill(bytes);
        return new RendezvousSharedSecret(bytes);
    }
}
