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
    private readonly IAnnouncerViewmodelFactory _announcerViewmodelFactory;
    private readonly IChatViewmodelFactory _chatViewmodelFactory;
    private readonly IAnnouncerRepository _announcerRepository;
    private bool _isAnnouncing;
    public ObservableCollection<AnnouncerViewmodel> Announcers { get; } = new();
    public ReactiveProperty<AnnouncerViewmodel?> SelectedAnnouncer { get; } = new();

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
        IAnnouncerViewmodelFactory announcerViewmodelFactory,
        IChatViewmodelFactory chatViewmodelFactory,
        IAnnouncerRepository announcerRepository)
    {
        _mainService = mainService;
        _logger = logger;
        _announcerViewmodelFactory = announcerViewmodelFactory;
        _chatViewmodelFactory = chatViewmodelFactory;
        _announcerRepository = announcerRepository;
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
        _announcerRepository.AnnouncerAdded
            .ObserveOnCurrentDispatcher()
            .Subscribe(OnAnnouncerAdded);
        IsBroadcastListening = _mainService.BroadcastListen
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty();
        IsBroadcastListening.Subscribe(b => _mainService.BroadcastListen.Value = b);
        SelectedAnnouncer
            .Subscribe(a=> Chat.Value = a == null ? null : _chatViewmodelFactory.CreateChat(a.AnnouncerModel));
    }

    public BindableReactiveProperty<ChatViewmodel?> Chat { get; }= new();

    private void OnAnnouncerAdded(ByteString announcerId)
    {
        var announcer = _announcerRepository.Announcers[announcerId];
        var announcerVm = _announcerViewmodelFactory.Create(announcer);
        Announcers.Add(announcerVm);
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