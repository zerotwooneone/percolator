using System.ComponentModel;
using System.Runtime.CompilerServices;
using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public sealed class AnnouncerViewmodel : INotifyPropertyChanged
{
    public string PublicKey { get; }
    public ByteString PublicKeyBytes { get; }

    public IBindableReactiveProperty Nickname { get; }

    public IBindableReactiveProperty IpAddress { get; }
    public IBindableReactiveProperty Port { get; }
    
    public IBindableReactiveProperty ToolTip { get; }

    public AnnouncerViewmodel(AnnouncerModel announcer)
    {
        PublicKeyBytes = announcer.Identity;
        PublicKey = announcer.Identity.ToBase64();
        Nickname = announcer.Nickname.ToReadOnlyBindableReactiveProperty(announcer.Nickname.Value);
        //todo: update ip address if it changes
        IpAddress = new BindableReactiveProperty<string>(announcer.IpAddresses.Last().ToString());
        Port = announcer.Port.ToReadOnlyBindableReactiveProperty();
        ToolTip = announcer.Port.Select(p => $"{announcer.IpAddresses.Last()}:{p}").ToReadOnlyBindableReactiveProperty("");
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