namespace Percolator.Application.Security;

using System;
using System.Security.Cryptography;

public static class PayloadPadding
{
    public static int CalculatePaddedLength(int unpaddedLength)
    {
        if (unpaddedLength <= 256) return 256;
        if (unpaddedLength <= 512) return 512;
        if (unpaddedLength <= 1024) return 1024;
        if (unpaddedLength <= 2048) return 2048;
        if (unpaddedLength <= 4096) return 4096;
        return ((unpaddedLength + 4095) / 4096) * 4096;
    }

    /// <summary>
    /// Applies ISO/IEC 7816-4 padding (0x80 delimiter followed by 0x00 bytes) up to the stepped bucketed target size.
    /// </summary>
    public static byte[] ApplyPadding(ReadOnlySpan<byte> unpadded)
    {
        int targetLength = CalculatePaddedLength(unpadded.Length + 1);
        byte[] padded = new byte[targetLength];
        unpadded.CopyTo(padded);
        padded[unpadded.Length] = 0x80;
        return padded;
    }

    /// <summary>
    /// Removes ISO/IEC 7816-4 padding.
    /// </summary>
    public static ReadOnlySpan<byte> RemovePadding(ReadOnlySpan<byte> padded)
    {
        int i = padded.Length - 1;
        while (i >= 0 && padded[i] == 0x00)
        {
            i--;
        }
        if (i < 0 || padded[i] != 0x80)
        {
            throw new CryptographicException("Invalid payload padding delimiter.");
        }
        return padded.Slice(0, i);
    }
}
