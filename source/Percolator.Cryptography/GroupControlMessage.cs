using System.Text.Json.Serialization;

namespace Percolator.Cryptography;

internal class GroupControlMessage
{
    public string? OldGroupId { get; set; }
    public byte[] SessionKey { get; set; } = null!;
    public string GroupId { get; set; } = null!;
    public byte[]? Signature { get; set; }
}
