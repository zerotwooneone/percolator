using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Domain.Client;
using R3;

namespace Percolator.Desktop.Main;

public class MainWindowViewmodel : INotifyPropertyChanged
{
    private readonly MainService _mainService;
    private readonly ILogger<MainWindowViewmodel> _logger;
    private readonly IRemoteClientViewmodelFactory _remoteClientViewmodelFactory;
    private readonly IChatViewmodelFactory _chatViewmodelFactory;
    private readonly IRemoteClientRepository _remoteClientRepository;
    private readonly ISelfProvider _selfProvider;
    public ObservableCollection<RemoteClientViewmodel> RemoteClients { get; } = new();
    public ReactiveProperty<RemoteClientViewmodel?> SelectedAnnouncer { get; } = new();

    public BindableReactiveProperty<bool> IsBroadcastListening { get; }

    public BindableReactiveProperty<bool> IsAnnouncing { get; }
    
    public BindableReactiveProperty<bool> AllowIntroductions { get; }
    
    public BindableReactiveProperty<bool> AutoReplyIntroductions { get; }
    
    public BindableReactiveProperty<bool> EditSelfNickname { get; } = new();
    public BindableReactiveProperty<string> SelfNickname { get; } = new();
    
    public BaseCommand SaveSelfNicknameCommand { get; }
    public BaseCommand EditSelfNicknameCommand { get; }

    
    public MainWindowViewmodel(
        MainService mainService,
        ILogger<MainWindowViewmodel> logger,
        IRemoteClientViewmodelFactory remoteClientViewmodelFactory,
        IChatViewmodelFactory chatViewmodelFactory,
        IRemoteClientRepository remoteClientRepository,
        ISelfProvider selfProvider)
    {
        _mainService = mainService;
        _logger = logger;
        _remoteClientViewmodelFactory = remoteClientViewmodelFactory;
        _chatViewmodelFactory = chatViewmodelFactory;
        _remoteClientRepository = remoteClientRepository;
        _selfProvider = selfProvider;
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

        SaveSelfNicknameCommand = new BaseCommand(OnSaveSelfNickname);
        EditSelfNicknameCommand = new BaseCommand(OnEditSelfNickname);
        IsBroadcastListening = _mainService.BroadcastListen
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty();
        IsBroadcastListening.Subscribe(b => _mainService.BroadcastListen.Value = b);
        SelectedAnnouncer
            .Subscribe(a=> Chat.Value = a == null ? null : _chatViewmodelFactory.CreateChat(a.RemoteClientModel));
        SelfNickname.Value = _selfProvider.GetSelf().PreferredNickname.Value;

        IsAnnouncing = _selfProvider.GetSelf().BroadcastSelf
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty();
        IsAnnouncing.Subscribe(b =>
        {
            _selfProvider.GetSelf().BroadcastSelf.Value = b;
            if (b)
            {
                _mainService.Announce();
            }
            else
            {
                _mainService.StopAnnounce();
            }
        });
    }

    public BindableReactiveProperty<ChatViewmodel?> Chat { get; }= new();

    private void OnAnnouncerAdded(ByteString announcerId)
    {
        var announcer = _remoteClientRepository.GetClientByIdentity(announcerId);
        var announcerVm = _remoteClientViewmodelFactory.Create(announcer);
        RemoteClients.Add(announcerVm);
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
    
    private void OnEditSelfNickname(object? _)
    {
        EditSelfNickname.Value = true;
    }

    private void OnSaveSelfNickname(object? _)
    {
        EditSelfNickname.Value = false;
        if(string.IsNullOrWhiteSpace(SelfNickname.Value))
        {
            SelfNickname.Value = _selfProvider.GetSelf().PreferredNickname.Value;
            return;
        }

        _selfProvider.GetSelf().PreferredNickname.Value = SelfNickname.Value;
    }
}