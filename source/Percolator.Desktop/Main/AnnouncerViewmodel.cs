using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public sealed class AnnouncerViewmodel : INotifyPropertyChanged
{
    public string PublicKey { get; }
    public ByteString PublicKeyBytes { get; }

    public BindableReactiveProperty<string> Nickname { get; }

    public BindableReactiveProperty<string> IpAddress { get; }
    public BindableReactiveProperty<int> Port { get; }
    
    public BindableReactiveProperty<string> ToolTip { get; }
    public BindableReactiveProperty<Visibility> IntroduceVisible { get; }
    public BaseCommand IntroduceCommand { get; }

    public AnnouncerViewmodel(AnnouncerModel announcer)
    {
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
        IntroduceCommand = new BaseCommand(OnIntroduceClicked);
    }

    private void OnIntroduceClicked(object? obj)
    {
        throw new NotImplementedException();
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