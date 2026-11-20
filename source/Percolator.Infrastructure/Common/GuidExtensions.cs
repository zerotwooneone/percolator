namespace Percolator.Infrastructure.Common;

public static class GuidExtensions
{
    /// <summary>
    /// Formats the Guid into a 16-byte array matching RFC 4122 Big-Endian byte order.
    /// </summary>
    public static byte[] ToBigEndianByteArray(this Guid guid)
    {
        return guid.ToByteArray(bigEndian: true);
    }
}
