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
    private readonly AnnouncerModel _announcer;
    private readonly IAnnouncerService _announcerService;
    private readonly ILogger<AnnouncerViewmodel> _logger;
    public string PublicKey { get; }
    public ByteString PublicKeyBytes { get; }

    public BindableReactiveProperty<string> Nickname { get; }

    public BindableReactiveProperty<string> IpAddress { get; }
    public BindableReactiveProperty<int> Port { get; }
    
    public BindableReactiveProperty<string> ToolTip { get; }
    public BindableReactiveProperty<Visibility> IntroduceVisible { get; }
    public BaseCommand IntroduceCommand { get; }
    public  ReadOnlyReactiveProperty<bool> CanReplyIntroduce { get; }

    public AnnouncerViewmodel(
        AnnouncerModel announcer,
        IAnnouncerService announcerService,
        ILogger<AnnouncerViewmodel> logger)
    {
        _announcer = announcer;
        _announcerService = announcerService;
        _logger = logger;
        PublicKeyBytes = announcer.Identity;
        PublicKey = announcer.Identity.ToBase64();
        Nickname = announcer.Nickname.ToBindableReactiveProperty(announcer.Nickname.Value);
        //todo: update ip address if it changes
        IpAddress = new BindableReactiveProperty<string>(announcer.IpAddresses.Last().ToString());
        Port = announcer.Port.ToBindableReactiveProperty();
        ToolTip = announcer.Port.Select(p => $"{announcer.IpAddresses.Last()}:{p} {Environment.NewLine} {PublicKey}").ToBindableReactiveProperty("");

        IntroduceVisible = announcer.CanIntroduce
            .Select(b=> b ? Visibility.Visible : Visibility.Collapsed)
            .ToBindableReactiveProperty();
        CanReplyIntroduce = _announcer.CanChat
            .ObserveOnCurrentDispatcher()
            .ToReadOnlyReactiveProperty();
        IntroduceCommand = new BaseCommand(OnIntroduceClicked, _=>!IntroduceInProgress);
    }

    public bool IntroduceInProgress { get; private set; }

    private async void OnIntroduceClicked(object? obj)
    {
        if (!_announcer.CanIntroduce.CurrentValue || _announcer.SelectedIpAddress.CurrentValue == null)
        {
            return;
        }

        IntroduceInProgress = true;
        IntroduceCommand.RaiseCanExecuteChanged();
        try
        {
            if (CanReplyIntroduce.CurrentValue)
            {
                await _announcerService.SendReplyIntroduction(_announcer);
            }
            else
            {
                await _announcerService.SendIntroduction(_announcer.SelectedIpAddress.CurrentValue, Port.Value);
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