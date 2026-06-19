namespace Percolator.Chat.Messaging.ValueObjects
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
