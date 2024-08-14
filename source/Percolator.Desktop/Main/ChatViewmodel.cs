using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Domain.Chat;
using Percolator.Desktop.Domain.Client;
using R3;

namespace Percolator.Desktop.Main;

public class ChatViewmodel:IDisposable
{
    private readonly ChatModel _chatModel;
    private readonly IChatService _chatService;
    private readonly ILogger<ChatViewmodel> _logger;
    private readonly IDisposable _chatSubcription;
    public BindableReactiveProperty<bool> SendEnabled { get; }

    public ChatViewmodel(
        ChatModel chatModel,
        IChatService chatService,
        ILogger<ChatViewmodel> logger)
    {
        _chatModel = chatModel;
        _chatService = chatService;
        _logger = logger;
        _chatSubcription = _chatModel.ChatMessage
            .ObserveOnCurrentDispatcher()
            .Subscribe(OnReceivedChatMessage);

        SendCommand = new BaseCommand(OnSendClicked);
        SendEnabled = _chatModel.CanIntroduce
            .CombineLatest(_chatModel.IntroduceInProgress, (canIntroduce, introduceInProgress) => canIntroduce && !introduceInProgress)
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
            if (_chatModel.SessionKey.Value == null)
            {
                if (!await TryIntroduce())
                {
                    return;
                }
                await Task.Delay(500);
                if (_chatModel.SessionKey.Value == null)
                {
                    //todo:tell the user that they are not responding
                    return;
                }
            }
            await _chatService.SendChatMessage(_chatModel, Text.Value);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "error sending chat message"); 
        }
        
        OnReceivedChatMessage(new MessageModel(DateTime.Now, Text.Value,true));
        
        Text.Value="";
    }
    
    private async Task<bool> TryIntroduce(CancellationToken cancellationToken = default)
    {
        if (!_chatModel.CanIntroduce.CurrentValue || _chatModel.RemoteClientModel.SelectedIpAddress.CurrentValue == null)
        {
            return false;
        }

        _chatModel.IntroduceInProgress.Value = true;
        try
        {
            if (_chatModel.CanReplyIntroduce.CurrentValue)
            {
                return await _chatService.TrySendReplyIntroduction(_chatModel, cancellationToken);
                return true;
            }
            else
            {
                await _chatService.TrySendIntroduction(_chatModel,cancellationToken);
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
            _chatModel.IntroduceInProgress.Value = false;
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

    public void Dispose()
    {
        _chatSubcription.Dispose();
        SendEnabled.Dispose();
        Text.Dispose();
    }
}