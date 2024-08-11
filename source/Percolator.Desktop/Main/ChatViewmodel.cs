using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using R3;

namespace Percolator.Desktop.Main;

public class ChatViewmodel
{
    private readonly RemoteClientModel _remoteClientModel;
    private readonly IChatService _chatService;
    private readonly ILogger<ChatViewmodel> _logger;
    private readonly IDisposable _chatSubcription;
    public BindableReactiveProperty<bool> SendEnabled { get; }

    public ChatViewmodel(
        RemoteClientModel remoteClientModel,
        IChatService chatService,
        ILogger<ChatViewmodel> logger)
    {
        _remoteClientModel = remoteClientModel;
        _chatService = chatService;
        _logger = logger;
        _chatSubcription = _remoteClientModel.ChatMessage
            .ObserveOnCurrentDispatcher()
            .Subscribe(OnReceivedChatMessage);

        SendCommand = new BaseCommand(OnSendClicked);
        SendEnabled = _remoteClientModel.CanIntroduce
            .CombineLatest(_remoteClientModel.IntroduceInProgress, (canIntroduce, introduceInProgress) => canIntroduce && !introduceInProgress)
            .ToBindableReactiveProperty();
    }

    private async void OnSendClicked(object? obj)
    {
        if (string.IsNullOrWhiteSpace(Text.Value))
        {
            return;
        }

        if (!SendEnabled.CurrentValue)
        {
            return;
        }

        try
        {
            if (_remoteClientModel.SessionKey.Value == null)
            {
                if (!await TryIntroduce())
                {
                    return;
                }
                await Task.Delay(500);
                if (_remoteClientModel.SessionKey.Value == null)
                {
                    //todo:tell the user that they are not responding
                    return;
                }
            }
            await _chatService.SendChatMessage(_remoteClientModel, Text.Value);
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
        if (!_remoteClientModel.CanIntroduce.CurrentValue || _remoteClientModel.SelectedIpAddress.CurrentValue == null)
        {
            return false;
        }

        if (!_chatService.TryGetIpAddress(out var sourceIp))
        {
            _logger.LogWarning("Failed to get ip address");
            return false;
        }

        _remoteClientModel.IntroduceInProgress.Value = true;
        try
        {
            if (_remoteClientModel.CanReplyIntroduce.CurrentValue)
            {
                await _chatService.SendReplyIntroduction(_remoteClientModel,sourceIp);
                return true;
            }
            else
            {
                await _chatService.SendIntroduction(_remoteClientModel.SelectedIpAddress.CurrentValue,_remoteClientModel.Port.Value, sourceIp);
                //todo:await response
                await Task.Delay(500);
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
            _remoteClientModel.IntroduceInProgress.Value = false;
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