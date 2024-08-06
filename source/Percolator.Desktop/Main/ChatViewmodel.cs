using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using R3;

namespace Percolator.Desktop.Main;

public class ChatViewmodel
{
    private readonly AnnouncerModel _announcerModel;
    private readonly IChatService _chatService;
    private readonly ILogger<ChatViewmodel> _logger;
    private readonly IDisposable _chatSubcription;

    public ChatViewmodel(
        AnnouncerModel announcerModel,
        IChatService chatService,
        ILogger<ChatViewmodel> logger)
    {
        _announcerModel = announcerModel;
        _chatService = chatService;
        _logger = logger;
        _chatSubcription = _announcerModel.ChatMessage.Subscribe(OnReceivedChatMessage);

        SendCommand = new BaseCommand(OnSendClicked);
    }

    private async void OnSendClicked(object? obj)
    {
        if (string.IsNullOrWhiteSpace(Text.Value))
        {
            return;
        }

        try
        {
            if (_announcerModel.SessionKey.Value == null)
            {
                //todo:introduce
                return;
            }
            await _chatService.SendChatMessage(_announcerModel, Text.Value);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "error sending chat message"); 
        }
        
        OnReceivedChatMessage(new MessageModel(DateTime.Now, Text.Value,true));
        
        Text.Value="";
    }

    private void OnReceivedChatMessage(MessageModel messageModel)
    {
        var messageViewmodel = new MessageViewmodel(messageModel);
        Messages.Add(messageViewmodel);
    }

    public ObservableCollection<MessageViewmodel> Messages { get; } = new();
    public BaseCommand SendCommand { get; }
    public BindableReactiveProperty<string> Text { get; }= new("");
}