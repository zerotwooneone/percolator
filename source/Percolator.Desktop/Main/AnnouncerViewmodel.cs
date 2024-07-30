using System.ComponentModel;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public sealed class AnnouncerViewmodel : INotifyPropertyChanged
{
    public string PublicKey { get; }
    public ByteString PublicKeyBytes { get; }

    public BindableReactiveProperty<string> Nickname { get; }

    public BindableReactiveProperty<string> Ephemeral { get; }

    public BindableReactiveProperty<string> IpAddress { get; }

    public AnnouncerViewmodel(AnnouncerModel announcer)
    {
        PublicKeyBytes = announcer.Identity;
        PublicKey = announcer.Identity.ToBase64();
        Nickname = announcer.Nickname.ToBindableReactiveProperty(announcer.Nickname.Value);
        Ephemeral = announcer.Ephemeral
            .Select(b=>b.ToBase64())
            .ToBindableReactiveProperty(announcer.Ephemeral.Value.ToBase64());
        //todo: update ip address if it changes
        IpAddress = new BindableReactiveProperty<string>(announcer.IpAddresses.Last().ToString());
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