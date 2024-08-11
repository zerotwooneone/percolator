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
        var selfModel = _selfProvider.GetSelf();
        AllowIntroductions = selfModel.IntroduceListen
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty(selfModel.IntroduceListen.Value);
        AllowIntroductions.Subscribe(b =>
        {
            selfModel.IntroduceListen.Value = b;
            if (b)
            {
                _mainService.ListenForIntroductions();
            }
            else
            {
                _mainService.StopListeningForIntroductions();
            }
        });
        AutoReplyIntroductions = selfModel.AutoReplyIntroductions
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty(selfModel.AutoReplyIntroductions.Value);
        AutoReplyIntroductions.Subscribe(b => selfModel.AutoReplyIntroductions.Value = b);
        _remoteClientRepository.ClientAdded
            .ObserveOnCurrentDispatcher()
            .Subscribe(OnAnnouncerAdded);
        foreach (var rc in _remoteClientRepository.GetAll())
        {
            OnAnnouncerAdded(rc.Identity);
        }

        SaveSelfNicknameCommand = new BaseCommand(OnSaveSelfNickname);
        EditSelfNicknameCommand = new BaseCommand(OnEditSelfNickname);
        IsBroadcastListening = selfModel.BroadcastListen
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty(selfModel.BroadcastListen.Value);
        IsBroadcastListening.Subscribe(b =>
        {
            selfModel.BroadcastListen.Value = b;
            if (b)
            {
                _mainService.ListenForBroadcasts();
            }
            else
            {
                _mainService.StopListeningForBroadcasts();
            }
        });
        SelectedAnnouncer
            .Subscribe(a=> Chat.Value = a == null ? null : _chatViewmodelFactory.CreateChat(a.RemoteClientModel));
        
        SelfNickname.Value = selfModel.PreferredNickname.Value;

        IsAnnouncing = selfModel.BroadcastSelf
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty(selfModel.BroadcastListen.Value);
        IsAnnouncing.Subscribe(b =>
        {
            selfModel.BroadcastSelf.Value = b;
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