namespace Percolator.Chat.ValueObjects
{
    public readonly struct GroupKeyVersion
    {
        public uint Value { get; }
        public GroupKeyVersion(uint value)
        {
            Value = value;
        }
        public override string ToString() => Value.ToString();
    }
}
