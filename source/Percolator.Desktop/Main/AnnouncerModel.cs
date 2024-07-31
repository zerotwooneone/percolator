using System.Net;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using R3;

namespace Percolator.Desktop.Main;

public class AnnouncerModel : IEquatable<AnnouncerModel>
{
    private readonly ILogger<AnnouncerModel> _logger;
    public ByteString Identity { get; }
    public ReactiveProperty<int> Port { get; }
    public ReactiveProperty<string> Nickname { get; }
    private List<IPAddress> _ipAddresses = new();
    public IReadOnlyCollection<IPAddress> IpAddresses => _ipAddresses;
    public ReadOnlyReactiveProperty<IPAddress?> SelectedIpAddress => _selectedIpAddress;
    private readonly ReactiveProperty<IPAddress?> _selectedIpAddress = new(null);
    public ReadOnlyReactiveProperty<bool> CanIntroduce { get; }

    public AnnouncerModel(
        ByteString identity,
        ILogger<AnnouncerModel> logger)
    {
        _logger = logger;
        Port = new ReactiveProperty<int>(Defaults.DefaultIntroducePort);
        Identity = identity;
        Nickname = new ReactiveProperty<string>(Identity.ToBase64());
        CanIntroduce = SelectedIpAddress
            .Select(ip => ip != null)
            .ToReadOnlyReactiveProperty();
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
        if (_ipAddresses.Count > sanityLimit)
        {
            _logger.LogWarning("too many ip addresses");
        }
        while (_ipAddresses.Count > sanityLimit)
        {
            _ipAddresses.RemoveAt(0);
        }
    }

    public void SelectIpAddress(int index)
    {
        if (index < 0 || index >= _ipAddresses.Count)
        {
            _logger.LogError("AnnouncerModel: invalid index {Index}", index);
            return;
        }
        if (_ipAddresses.Count == 0)
        {
            _selectedIpAddress.Value = null;
            return;
        }

        if (_ipAddresses.Count == 1)
        {
            _selectedIpAddress.Value = _ipAddresses[0];
            return;
        }

        _selectedIpAddress.Value = _ipAddresses[index];
    }

    public ReactiveProperty<DateTimeOffset> LastSeen { get; } = new();
}