using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Delegates;

namespace Percolator.Domain.Security.ValueObjects;

/// <summary>
/// A memory-scrubbing passphrase wrapper that extracts UTF-8 spans directly into temporary stack or rented buffers
/// without leaving string or byte artifacts on the managed garbage collector heap.
/// </summary>
public sealed class VaultPassphrase : ISensitiveSecret
{
    private char[] _chars;

    /// <summary>
    /// Gets a value indicating whether this passphrase's memory has been scrubbed and zeroized.
    /// </summary>
    public bool IsZeroized { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this passphrase is empty (0 characters).
    /// </summary>
    public bool IsEmpty => _chars.Length == 0;

    /// <summary>
    /// Gets the character length of the passphrase.
    /// </summary>
    public int Length => _chars.Length;

    private VaultPassphrase(char[] chars)
    {
        _chars = chars;
    }

    /// <summary>
    /// Creates a new <see cref="VaultPassphrase"/> instance from the given string.
    /// If <paramref name="input"/> is null, creates an empty passphrase.
    /// </summary>
    /// <param name="input">The plaintext passphrase string, or null.</param>
    /// <returns>A new <see cref="VaultPassphrase"/> instance.</returns>
    public static VaultPassphrase Create(string? input) =>
        new(input?.ToCharArray() ?? Array.Empty<char>());

    /// <summary>
    /// Creates a new <see cref="VaultPassphrase"/> instance from a character span.
    /// </summary>
    /// <param name="input">The character span.</param>
    /// <returns>A new <see cref="VaultPassphrase"/> instance.</returns>
    public static VaultPassphrase Create(ReadOnlySpan<char> input) =>
        new(input.ToArray());

    /// <summary>
    /// Gets a shared empty <see cref="VaultPassphrase"/> instance.
    /// </summary>
    public static VaultPassphrase Empty => new(Array.Empty<char>());

    /// <summary>
    /// Executes an action with the UTF-8 encoded bytes of the passphrase in a temporary stack or rented buffer,
    /// deterministically zeroizing the buffer immediately upon completion even if an exception is thrown.
    /// </summary>
    /// <param name="action">The callback to execute with the UTF-8 byte span.</param>
    public void WithUtf8Span(Action<ReadOnlySpan<byte>> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        WithUtf8Span(span =>
        {
            action(span);
            return 0;
        });
    }

    /// <summary>
    /// Executes a function with the UTF-8 encoded bytes of the passphrase in a temporary stack or rented buffer,
    /// deterministically zeroizing the buffer immediately upon completion and returning the result.
    /// </summary>
    /// <typeparam name="TResult">The return type of the delegate.</typeparam>
    /// <param name="func">The function to execute with the UTF-8 byte span.</param>
    /// <returns>The result computed by <paramref name="func"/>.</returns>
    public TResult WithUtf8Span<TResult>(ReadOnlySpanFunc<byte, TResult> func)
    {
        ArgumentNullException.ThrowIfNull(func);
        ObjectDisposedException.ThrowIf(IsZeroized, this);

        int maxByteCount = Encoding.UTF8.GetMaxByteCount(_chars.Length);
        Span<byte> buffer = maxByteCount <= 256 ? stackalloc byte[maxByteCount] : new byte[maxByteCount];
        try
        {
            int written = Encoding.UTF8.GetBytes(_chars.AsSpan(), buffer);
            return func(buffer[..written]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    /// <summary>
    /// Evaluates whether two passphrases are identical in constant time.
    /// </summary>
    /// <param name="a">The first passphrase.</param>
    /// <param name="b">The second passphrase.</param>
    /// <returns><c>true</c> if both passphrases have identical UTF-8 representations; otherwise, <c>false</c>.</returns>
    public static bool ConstantTimeEquals(VaultPassphrase a, VaultPassphrase b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        ObjectDisposedException.ThrowIf(a.IsZeroized, a);
        ObjectDisposedException.ThrowIf(b.IsZeroized, b);

        int maxA = Encoding.UTF8.GetMaxByteCount(a._chars.Length);
        Span<byte> bufA = maxA <= 256 ? stackalloc byte[maxA] : new byte[maxA];

        int maxB = Encoding.UTF8.GetMaxByteCount(b._chars.Length);
        Span<byte> bufB = maxB <= 256 ? stackalloc byte[maxB] : new byte[maxB];

        try
        {
            int writtenA = Encoding.UTF8.GetBytes(a._chars.AsSpan(), bufA);
            int writtenB = Encoding.UTF8.GetBytes(b._chars.AsSpan(), bufB);

            return CryptographicOperations.FixedTimeEquals(bufA[..writtenA], bufB[..writtenB]);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bufA);
            CryptographicOperations.ZeroMemory(bufB);
        }
    }

    /// <summary>
    /// Deterministically scrubs the underlying memory holding the passphrase characters and marks the instance as disposed.
    /// </summary>
    public void Zeroize()
    {
        if (IsZeroized) return;

        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_chars.AsSpan()));
        _chars = Array.Empty<char>();
        IsZeroized = true;
    }

    /// <summary>
    /// Disposes this instance by calling <see cref="Zeroize"/>.
    /// </summary>
    public void Dispose() => Zeroize();

    /// <inheritdoc />
    public override string ToString() => IsZeroized ? "VaultPassphrase [DISPOSED]" : "VaultPassphrase [PROTECTED]";
}
