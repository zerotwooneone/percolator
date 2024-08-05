using System.Collections.ObjectModel;
using R3;

namespace Percolator.Desktop.Main;

public class ChatViewmodel
{
    private readonly AnnouncerModel _announcerModel;
    private readonly IDisposable _chatSubcription;

    public ChatViewmodel(AnnouncerModel announcerModel)
    {
        _announcerModel = announcerModel;
        _chatSubcription = _announcerModel.ChatMessage.Subscribe(OnReceivedChatMessage);
    }
    
    private void OnReceivedChatMessage(MessageModel messageModel)
    {
        var messageViewmodel = new MessageViewmodel(messageModel);
        Messages.Add(messageViewmodel);
    }

    public ObservableCollection<MessageViewmodel> Messages { get; } = new();
}