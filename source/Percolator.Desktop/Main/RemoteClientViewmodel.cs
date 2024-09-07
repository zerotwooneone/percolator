using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Domain.Chat;
using Percolator.Desktop.Domain.Client;
using Percolator.Desktop.Udp;
using R3;

namespace Percolator.Desktop.Main;

public sealed class RemoteClientViewmodel : INotifyPropertyChanged
{
    public RemoteClientModel RemoteClientModel{get;}
    private readonly IRemoteClientService _remoteClientService;
    private readonly ILogger<RemoteClientViewmodel> _logger;
    private readonly ChatRepository _chatRepository;
    public string PublicKey { get; }
    public ByteString PublicKeyBytes { get; }

    public BindableReactiveProperty<string> Nickname { get; }

    public BindableReactiveProperty<string?> IpAddress { get; }
    public BindableReactiveProperty<int> Port { get; }
    
    public BindableReactiveProperty<string> ToolTip { get; }
    public BaseCommand IntroduceCommand { get; }

    public RemoteClientViewmodel(
        RemoteClientModel remoteClientModel,
        IRemoteClientService remoteClientService,
        ILogger<RemoteClientViewmodel> logger,
        ChatRepository chatRepository)
    {
        RemoteClientModel = remoteClientModel;
        _remoteClientService = remoteClientService;
        _logger = logger;
        _chatRepository = chatRepository;
        PublicKeyBytes = remoteClientModel.Identity;
        PublicKey = remoteClientModel.Identity.ToBase64();
        Nickname = remoteClientModel.PreferredNickname
            .ToBindableReactiveProperty(remoteClientModel.PreferredNickname.Value);
        IpAddress = new BindableReactiveProperty<string?>(remoteClientModel.IpAddresses.LastOrDefault()?.ToString());
        Port = remoteClientModel.Port.ToBindableReactiveProperty();
        ToolTip = remoteClientModel.Port.Select(p => $"{remoteClientModel.SelectedIpAddress.CurrentValue}:{p} {Environment.NewLine} {PublicKey}").ToBindableReactiveProperty("");

        IntroduceCommand = new BaseCommand(OnIntroduceClicked, _=>!IntroduceInProgress);
    }

    public bool IntroduceInProgress { get; private set; }

    private async void OnIntroduceClicked(object? obj)
    {
        if (!_chatRepository.TryGetByIdentity(RemoteClientModel.Identity, out var chatModel))
        {
            return;
        }
        if (!chatModel.CanIntroduce.CurrentValue || RemoteClientModel.SelectedIpAddress.CurrentValue == null)
        {
            return;
        }

        IntroduceInProgress = true;
        IntroduceCommand.RaiseCanExecuteChanged();
        try
        {
            if (chatModel.CanReplyIntroduce.CurrentValue)
            {
                await _remoteClientService.TrySendReplyIntroduction(chatModel);
            }
            else
            {
                await _remoteClientService.TrySendIntroduction(chatModel);
            }
            
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Failed to introduce");
        }
        finally
        {
            IntroduceInProgress = false;
            IntroduceCommand.RaiseCanExecuteChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}