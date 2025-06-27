namespace Percolator.Cryptography;

public class UnsignedGroupControlMessage
{
    public string? OldGroupId { get; set; }
    public byte[]? SessionKey { get; set; }
    public string GroupId { get; set; } = string.Empty;
}

public class SignedGroupControlMessage
{
    public byte[] UnsignedMessage { get; set; } = Array.Empty<byte>();
    public byte[] Signature { get; set; } = Array.Empty<byte>();
}