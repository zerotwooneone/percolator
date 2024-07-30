using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Percolator.Desktop.Main;

public class MainWindowViewmodel : INotifyPropertyChanged
{
    private readonly MainService _mainService;
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

    public MainWindowViewmodel(MainService mainService)
    {
        _mainService = mainService;
        ListenCommand = new BaseCommand(OnListen);
        AnnounceCommand = new BaseCommand(OnAnnounce);
    }

    private async void OnListen(object? obj)
    {
        await _ListenCts.CancelAsync();
        if (IsListening)
        {
            return;
        }
        _ListenCts = new CancellationTokenSource();
        await _mainService.Listen(_ListenCts.Token);
    }

    private void OnAnnounce(object? obj)
    {
        throw new NotImplementedException();
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