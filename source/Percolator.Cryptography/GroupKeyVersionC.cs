namespace Percolator.Cryptography
{
    // Cryptography-domain value object for group key version
    public readonly record struct GroupKeyVersionC(uint Value)
    {
        public override string ToString() => Value.ToString();
    }
}
