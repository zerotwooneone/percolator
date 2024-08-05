namespace Percolator.Desktop.Main;

public class MessageModel
{
    public MessageModel(DateTime received, string message)
    {
        Received = received;
        Message = message;
    }

    public DateTime Received { get; }
    public string Message { get; }
}