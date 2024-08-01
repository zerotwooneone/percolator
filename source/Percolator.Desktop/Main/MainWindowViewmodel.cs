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
    private bool _isBroadcastListening;
    private bool _isAnnouncing;
    public ObservableCollection<AnnouncerViewmodel> Announcers { get; } = new();
    public ReactiveProperty<AnnouncerViewmodel> SelectedAnnouncer { get; } = new();
    
    public BaseCommand ListenBroadcastCommand { get;  }

    public bool IsBroadcastListening
    {
        get => _isBroadcastListening;
        set => SetField(ref _isBroadcastListening, value);
    }

    public BaseCommand AnnounceCommand { get; }

    public bool IsAnnouncing
    {
        get => _isAnnouncing;
        set => SetField(ref _isAnnouncing, value);
    }
    
    public BaseCommand AllowIntroductionsCommand { get; }
    public BindableReactiveProperty<bool> AllowIntroductions { get; } = new();
    
    public BindableReactiveProperty<bool> AutoReplyIntroductions { get; }

    public MainWindowViewmodel(
        MainService mainService,
        ILogger<MainWindowViewmodel> logger,
        IAnnouncerViewmodelFactory announcerViewmodelFactory)
    {
        _mainService = mainService;
        _logger = logger;
        _announcerViewmodelFactory = announcerViewmodelFactory;
        ListenBroadcastCommand = new BaseCommand(OnListenClicked);
        AnnounceCommand = new BaseCommand(OnAnnounceClicked);
        AllowIntroductionsCommand = new BaseCommand(OnAllowIntroductionsClicked);
        AutoReplyIntroductions = _mainService.AutoReplyIntroductions
            .ObserveOnCurrentDispatcher()
            .ToBindableReactiveProperty();
        _mainService.AnnouncerAdded
            .ObserveOnCurrentDispatcher()
            .Subscribe(OnAnnouncerAdded);
    }

    private void OnAnnouncerAdded(ByteString announcerId)
    {
        var announcer = _mainService.Announcers[announcerId];
        var announcerVm = _announcerViewmodelFactory.Create(announcer);
        Announcers.Add(announcerVm);
    }

    private void OnListenClicked(object? obj)
    {
        //IsListening changes before this is called, so logic is inverted
        if (IsBroadcastListening)
        {
            _mainService.BroadcastListen();
        }
        else
        {
            _mainService.StopBroadcastListen();
        }
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
    
    private void OnAllowIntroductionsClicked(object? obj)
    {
        //value changes before this is called, so logic is inverted
        if (AllowIntroductions.Value)
        {
            _mainService.BeginIntroduceListen();
        }
        else
        {
            _mainService.StopIntroduceListen();
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