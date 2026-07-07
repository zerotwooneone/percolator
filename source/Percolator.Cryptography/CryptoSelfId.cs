namespace Percolator.Cryptography;

public readonly record struct CryptoSelfId(uint Value)
{
    public override string ToString() => Value.ToString();
}
