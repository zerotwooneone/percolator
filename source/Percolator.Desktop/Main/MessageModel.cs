namespace Percolator.Desktop.Main;

public class MessageModel
{
    public MessageModel(DateTime received, string messae)
    {
        Received = received;
        Messae = messae;
    }

    public DateTime Received { get; }
    public string Messae { get; }
}