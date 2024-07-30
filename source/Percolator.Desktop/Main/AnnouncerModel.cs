using System.Net;
using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public class AnnouncerModel : IEquatable<AnnouncerModel>
{
    public ByteString Identity { get; }
    public ReactiveProperty<ByteString> Ephemeral { get; }
    public ReactiveProperty<string> Nickname { get; }
    private List<IPAddress> _ipAddresses = new();
    public IReadOnlyCollection<IPAddress> IpAddresses => _ipAddresses;

    public AnnouncerModel(ByteString identity, ByteString ephemeral)
    {
        Ephemeral = new ReactiveProperty<ByteString>(ephemeral);
        Identity = identity;
        Nickname = new ReactiveProperty<string>(Identity.ToBase64());
    }
    
    public override string ToString()
    {
        return Identity.ToBase64();
    }

    public override bool Equals(object? obj)
    {
        if (obj is AnnouncerModel other)
        {
            return Identity.Equals(other.Identity);
        }
        return false;
    }

    public bool Equals(AnnouncerModel? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Identity.Equals(other.Identity);
    }
    
    public override int GetHashCode()
    {
        return Identity.GetHashCode();
    }

    public static bool operator ==(AnnouncerModel? left, AnnouncerModel? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(AnnouncerModel? left, AnnouncerModel? right)
    {
        return !Equals(left, right);
    }

    public void AddIpAddress(IPAddress address)
    {
        if(_ipAddresses.Contains(address)){
            return;
        }
        _ipAddresses.Add(address);
        const int sanityLimit = 10;
        while (_ipAddresses.Count > sanityLimit)
        {
            _ipAddresses.RemoveAt(0);
        }
    }

    public ReactiveProperty<DateTimeOffset> LastSeen { get; } = new();
}