namespace Percolator.Desktop.Main;

public class MessageViewmodel
{
    public MessageViewmodel(MessageModel messageModel)
    {
        Received = messageModel.Received;
        Text = messageModel.Messae;
    }

    public DateTime Received { get; }
    public string Text { get; }
}