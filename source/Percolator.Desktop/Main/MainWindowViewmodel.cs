using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Percolator.Desktop.Main;

public class MainWindowViewmodel : INotifyPropertyChanged
{
    private readonly MainService _mainService;
    private readonly ILogger<MainWindowViewmodel> _logger;
    private bool _isListening;
    private bool _isAnnouncing;
    
    public BaseCommand ListenCommand { get;  }

    public bool IsListening
    {
        get => _isListening;
        set => SetField(ref _isListening, value);
    }

    public BaseCommand AnnounceCommand { get; }

    public bool IsAnnouncing
    {
        get => _isAnnouncing;
        set => SetField(ref _isAnnouncing, value);
    }

    public MainWindowViewmodel(
        MainService mainService,
        ILogger<MainWindowViewmodel> logger)
    {
        _mainService = mainService;
        _logger = logger;
        ListenCommand = new BaseCommand(OnListenClicked);
        AnnounceCommand = new BaseCommand(OnAnnounceClicked);
    }

    private void OnListenClicked(object? obj)
    {
        //IsListening changes before this is called, so logic is inverted
        if (IsListening)
        {
            _mainService.Listen();
        }
        else
        {
            _mainService.StopListen();
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