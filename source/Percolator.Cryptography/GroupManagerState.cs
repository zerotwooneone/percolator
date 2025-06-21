namespace Percolator.Cryptography;

internal class GroupManagerState
{
    public byte[] SigningKeyPrivate { get; set; } = null!;
    public string GroupId { get; set; } = null!;
    public byte[] GroupSessionState { get; set; } = null!;
    public Dictionary<string, byte[]> MemberSessionStates { get; set; } = new();
}