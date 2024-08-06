using System.Windows;

namespace Percolator.Desktop.Main;

public class MessageViewmodel
{
    public MessageViewmodel(MessageModel messageModel)
    {
        Received = messageModel.Received.ToString("HH:mm:ss");
        Text = messageModel.Message;
        Alignment = messageModel.IsSelf ? TextAlignment.Right : TextAlignment.Left;
    }

    public TextAlignment Alignment { get; }

    public string Received { get; }
    public string Text { get; }
}