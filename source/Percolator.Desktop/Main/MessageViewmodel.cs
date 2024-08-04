namespace Percolator.Desktop.Main;

public class MessageViewmodel
{
    public MessageViewmodel(DateTime received, string text)
    {
        Received = received;
        Text = text;
    }

    public DateTime Received { get; }
    public string Text { get; }
}