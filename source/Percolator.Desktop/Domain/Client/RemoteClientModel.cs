using System.Net;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Percolator.Desktop.Domain.Chat;
using Percolator.Desktop.Main;
using R3;

namespace Percolator.Desktop.Domain.Client;

public class RemoteClientModel : IEquatable<RemoteClientModel>
{
    private readonly ILogger<RemoteClientModel> _logger;
    public ByteString Identity { get; }
    public ReactiveProperty<int> Port { get; }
    public ReactiveProperty<string> PreferredNickname { get; }
    private List<IPAddress> _ipAddresses = new();
    public IReadOnlyCollection<IPAddress> IpAddresses => _ipAddresses;
    public ReadOnlyReactiveProperty<IPAddress?> SelectedIpAddress => _selectedIpAddress;
    private readonly ReactiveProperty<IPAddress?> _selectedIpAddress = new(null);

    public RemoteClientModel(
        ByteString identity,
        ILogger<RemoteClientModel> logger,
        int? port = null,
        string? nickname = null)
    {
        _logger = logger;
        Port = new ReactiveProperty<int>( port ?? Defaults.DefaultIntroducePort);
        Identity = identity;
        PreferredNickname = new SynchronizedReactiveProperty<string>(String.IsNullOrWhiteSpace(nickname) ? Identity.ToBase64(): nickname);
    }
    
    public override string ToString()
    {
        return Identity.ToBase64();
    }

    public override bool Equals(object? obj)
    {
        if (obj is RemoteClientModel other)
        {
            return Identity.Equals(other.Identity);
        }
        return false;
    }

    public bool Equals(RemoteClientModel? other)
    {
        if (ReferenceEquals(null, other)) return false;
        if (ReferenceEquals(this, other)) return true;
        return Identity.Equals(other.Identity);
    }
    
    public override int GetHashCode()
    {
        return Identity.GetHashCode();
    }

    public static bool operator ==(RemoteClientModel? left, RemoteClientModel? right)
    {
        return Equals(left, right);
    }

    public static bool operator !=(RemoteClientModel? left, RemoteClientModel? right)
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
            while (_ipAddresses.Count > sanityLimit)
            {
                _ipAddresses.RemoveAt(0);
            }
        }
        if(_ipAddresses.Count == 1)
        {
            _selectedIpAddress.Value = _ipAddresses[0];
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

    public ReactiveProperty<DateTimeOffset> LastSeen { get; } = new SynchronizedReactiveProperty<DateTimeOffset>();
    
}