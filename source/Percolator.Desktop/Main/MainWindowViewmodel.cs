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
    private CancellationTokenSource _ListenCts=new();
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
        ListenCommand = new BaseCommand(OnListen);
        AnnounceCommand = new BaseCommand(OnAnnounce);
    }

    private async void OnListen(object? obj)
    {
        await _ListenCts.CancelAsync();
        if (!IsListening)
        {
            return;
        }
        _ListenCts = new CancellationTokenSource();
        await _mainService.Listen(_ListenCts.Token);
    }

    private async void OnAnnounce(object? obj)
    {
        //await _AnnounceCts.CancelAsync();
        
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