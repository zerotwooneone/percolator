using System.Text.Json.Serialization;

namespace Percolator.Cryptography;

internal class GroupControlMessage
{
    [JsonInclude]
    public byte[] SessionKey { get; set; } = null!;

    [JsonInclude]
    public string GroupId { get; set; } = null!;

    [JsonInclude]
    public byte[] Signature { get; set; } = null!;
}
