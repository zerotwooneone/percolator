namespace Percolator.Crypto;

public record HeaderWrapper
{
    public HeaderType? Header { get; init; }
    public AssociatedDataType? AssociatedData { get; init; }
    
    public record HeaderType
    {
        public byte[]? PublicKey { get; init; }
        public uint? PreviousChainLength { get; init; }
        public uint? MessageNumber { get; init; }
    }
    
    public record AssociatedDataType
    {
        public byte[]? Data { get; init; }
    }
}