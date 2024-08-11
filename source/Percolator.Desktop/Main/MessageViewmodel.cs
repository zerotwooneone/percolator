using System.Windows;
using System.Windows.Media;
using Percolator.Desktop.Domain.Chat;

namespace Percolator.Desktop.Main;

public class MessageViewmodel
{
    public MessageViewmodel(MessageModel messageModel)
    {
        Received = messageModel.Received.ToString("HH:mm:ss");
        Text = messageModel.Message;
        Alignment = messageModel.IsSelf ? TextAlignment.Right : TextAlignment.Left;
        Background = messageModel.IsSelf ? new SolidColorBrush(Colors.LightBlue) : new SolidColorBrush(Colors.LightGreen);
    }

    public TextAlignment Alignment { get; }
    public SolidColorBrush Background { get; } 

    public string Received { get; }
    public string Text { get; }
}