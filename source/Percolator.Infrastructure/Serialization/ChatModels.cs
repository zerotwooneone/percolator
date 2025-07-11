namespace Percolator.Infrastructure.Serialization;

public class ConversationModel
{
    public Guid Id { get; set; }
    public byte[] ChannelId { get; set; } = Array.Empty<byte>();
    public List<Guid> Participants { get; set; } = new();
    public List<MessageModel> Messages { get; set; } = new();
    public string? Name { get; set; }
}

public class MessageModel
{
    public Guid Id { get; set; }
    public Guid Sender { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public string Body { get; set; } = string.Empty;
}
