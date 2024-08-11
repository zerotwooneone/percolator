using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Domain.Client;
using R3;

namespace Percolator.Desktop.Main;

public class MainDefaultViewmodel : INotifyPropertyChanged
{
    private readonly MainService _mainService;
    private readonly ILogger<MainWindowViewmodel> _logger;
    private readonly IRemoteClientViewmodelFactory _remoteClientViewmodelFactory;
    private readonly IChatViewmodelFactory _chatViewmodelFactory;
    private readonly IRemoteClientRepository _remoteClientRepository;
    private readonly ISelfProvider _selfProvider;
    public ObservableCollection<RemoteClientViewmodel> RemoteClients { get; } = new();
    public ReactiveProperty<RemoteClientViewmodel?> SelectedAnnouncer { get; } = new();

    

    
    public MainDefaultViewmodel(
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
        
        _remoteClientRepository.ClientAdded
            .ObserveOnCurrentDispatcher()
            .Subscribe(OnAnnouncerAdded);
        foreach (var rc in _remoteClientRepository.GetAll())
        {
            OnAnnouncerAdded(rc.Identity);
        }

        
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