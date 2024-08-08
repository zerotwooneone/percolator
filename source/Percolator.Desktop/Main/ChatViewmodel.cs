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
        _chatSubcription = _announcerModel.ChatMessage
            .ObserveOnCurrentDispatcher()
            .Subscribe(OnReceivedChatMessage);

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
                if (!await TryIntroduce())
                {
                    return;
                }
                await Task.Delay(500);
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
    
    private async Task<bool> TryIntroduce()
    {
        if (!_announcerModel.CanIntroduce.CurrentValue || _announcerModel.SelectedIpAddress.CurrentValue == null)
        {
            return false;
        }

        if (!_chatService.TryGetIpAddress(out var sourceIp))
        {
            _logger.LogWarning("Failed to get ip address");
            return false;
        }

        _announcerModel.IntroduceInProgress.Value = true;
        try
        {
            if (_announcerModel.CanReplyIntroduce.CurrentValue)
            {
                await _chatService.SendReplyIntroduction(_announcerModel,sourceIp);
                return true;
            }
            else
            {
                await _chatService.SendIntroduction(_announcerModel.SelectedIpAddress.CurrentValue,_announcerModel.Port.Value, sourceIp);
                //todo:await response
                return true;
            }
            
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to introduce");
            return false;
        }
        finally
        {
            _announcerModel.IntroduceInProgress.Value = false;
        }
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