using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Udp;
using R3;

namespace Percolator.Desktop.Main;

public sealed class AnnouncerViewmodel : INotifyPropertyChanged
{
    public RemoteClientModel RemoteClientModel{get;}
    private readonly IAnnouncerService _announcerService;
    private readonly ILogger<AnnouncerViewmodel> _logger;
    public string PublicKey { get; }
    public ByteString PublicKeyBytes { get; }

    public BindableReactiveProperty<string> Nickname { get; }

    public BindableReactiveProperty<string?> IpAddress { get; }
    public BindableReactiveProperty<int> Port { get; }
    
    public BindableReactiveProperty<string> ToolTip { get; }
    public BindableReactiveProperty<Visibility> IntroduceVisible { get; }
    public BaseCommand IntroduceCommand { get; }
    public  ReadOnlyReactiveProperty<bool> CanReplyIntroduce { get; }

    public AnnouncerViewmodel(
        RemoteClientModel remoteClientModel,
        IAnnouncerService announcerService,
        ILogger<AnnouncerViewmodel> logger)
    {
        RemoteClientModel = remoteClientModel;
        _announcerService = announcerService;
        _logger = logger;
        PublicKeyBytes = remoteClientModel.Identity;
        PublicKey = remoteClientModel.Identity.ToBase64();
        Nickname = remoteClientModel.PreferredNickname
            .ToBindableReactiveProperty(remoteClientModel.PreferredNickname.Value);
        IpAddress = new BindableReactiveProperty<string?>(remoteClientModel.IpAddresses.LastOrDefault()?.ToString());
        Port = remoteClientModel.Port.ToBindableReactiveProperty();
        ToolTip = remoteClientModel.Port.Select(p => $"{remoteClientModel.SelectedIpAddress.CurrentValue}:{p} {Environment.NewLine} {PublicKey}").ToBindableReactiveProperty("");

        IntroduceVisible = remoteClientModel.CanIntroduce
            .Select(b=> b ? Visibility.Visible : Visibility.Collapsed)
            .ToBindableReactiveProperty();
        CanReplyIntroduce = RemoteClientModel.CanReplyIntroduce
            .ObserveOnCurrentDispatcher()
            .ToReadOnlyReactiveProperty();
        IntroduceCommand = new BaseCommand(OnIntroduceClicked, _=>!IntroduceInProgress);
    }

    public bool IntroduceInProgress { get; private set; }

    private async void OnIntroduceClicked(object? obj)
    {
        if (!RemoteClientModel.CanIntroduce.CurrentValue || RemoteClientModel.SelectedIpAddress.CurrentValue == null)
        {
            return;
        }

        if (!_announcerService.TryGetIpAddress(out var sourceIp))
        {
            _logger.LogWarning("Failed to get ip address");
            return;
        }

        IntroduceInProgress = true;
        IntroduceCommand.RaiseCanExecuteChanged();
        try
        {
            if (CanReplyIntroduce.CurrentValue)
            {
                await _announcerService.SendReplyIntroduction(RemoteClientModel,sourceIp);
            }
            else
            {
                await _announcerService.SendIntroduction(RemoteClientModel.SelectedIpAddress.CurrentValue, Port.Value, sourceIp);
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