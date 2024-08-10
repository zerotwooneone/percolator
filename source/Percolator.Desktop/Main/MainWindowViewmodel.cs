using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using R3;

namespace Percolator.Desktop.Main;

public class MainWindowViewmodel : INotifyPropertyChanged
{
    private readonly MainService _mainService;
    private readonly ILogger<MainWindowViewmodel> _logger;
    private readonly IRemoteClientViewmodelFactory _remoteClientViewmodelFactory;
    private readonly IChatViewmodelFactory _chatViewmodelFactory;
    private readonly IRemoteClientRepository _remoteClientRepository;
    private bool _isAnnouncing;
    public ObservableCollection<RemoteClientViewmodel> RemoteClients { get; } = new();
    public ReactiveProperty<RemoteClientViewmodel?> SelectedAnnouncer { get; } = new();

    public BindableReactiveProperty<bool> IsBroadcastListening { get; }

    public BaseCommand AnnounceCommand { get; }

    public bool IsAnnouncing
    {
        get => _isAnnouncing;
        set => SetField(ref _isAnnouncing, value);
    }
    
    public BindableReactiveProperty<bool> AllowIntroductions { get; }
    
    public BindableReactiveProperty<bool> AutoReplyIntroductions { get; }

    public MainWindowViewmodel(
        MainService mainService,
        ILogger<MainWindowViewmodel> logger,
        IRemoteClientViewmodelFactory remoteClientViewmodelFactory,
        IChatViewmodelFactory chatViewmodelFactory,
        IRemoteClientRepository remoteClientRepository)
    {
        _mainService = mainService;
        _logger = logger;
        _remoteClientViewmodelFactory = remoteClientViewmodelFactory;
        _chatViewmodelFactory = chatViewmodelFactory;
        _remoteClientRepository = remoteClientRepository;
        AnnounceCommand = new BaseCommand(OnAnnounceClicked);
        AllowIntroductions = _mainService.ListenForIntroductions
            .ToBindableReactiveProperty();
        AllowIntroductions.Subscribe(b =>
        {
            _mainService.ListenForIntroductions.Value = b;
        });
        AutoReplyIntroductions = _mainService.AutoReplyIntroductions
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty();
        AutoReplyIntroductions.Subscribe(b => _mainService.AutoReplyIntroductions.Value = b);
        _remoteClientRepository.ClientAdded
            .ObserveOnCurrentDispatcher()
            .Subscribe(OnAnnouncerAdded);
        foreach (var rc in _remoteClientRepository.GetAll())
        {
            OnAnnouncerAdded(rc.Identity);
        }
        IsBroadcastListening = _mainService.BroadcastListen
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty();
        IsBroadcastListening.Subscribe(b => _mainService.BroadcastListen.Value = b);
        SelectedAnnouncer
            .Subscribe(a=> Chat.Value = a == null ? null : _chatViewmodelFactory.CreateChat(a.RemoteClientModel));
    }

    public BindableReactiveProperty<ChatViewmodel?> Chat { get; }= new();

    private void OnAnnouncerAdded(ByteString announcerId)
    {
        var announcer = _remoteClientRepository.GetClientByIdentity(announcerId);
        var announcerVm = _remoteClientViewmodelFactory.Create(announcer);
        RemoteClients.Add(announcerVm);
    }

    private void OnAnnounceClicked(object? obj)
    {
        //IsAnnouncing changes before this is called, so logic is inverted
        if (IsAnnouncing)
        {
             _mainService.Announce();
        }
        else
        {
            _mainService.StopAnnounce();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}