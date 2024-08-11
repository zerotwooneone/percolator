namespace Percolator.Desktop.Domain.Chat;

public class MessageModel
{
    public MessageModel(DateTime received, string message,bool isSelf)
    {
        Received = received;
        Message = message;
        IsSelf = isSelf;
    }

    public DateTime Received { get; }
    public string Message { get; }
    public bool IsSelf { get; }
}