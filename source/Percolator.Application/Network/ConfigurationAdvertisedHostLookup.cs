using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;

namespace Percolator.Application.Network;

public sealed class ConfigurationAdvertisedHostLookup : IAdvertisedHostLookup
{
    private readonly IOptions<TransportOptions> _transportOptions;

    public ConfigurationAdvertisedHostLookup(IOptions<TransportOptions> transportOptions)
    {
        _transportOptions = transportOptions;
    }

    public Task<string> GetAdvertisedHostAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var host = _transportOptions.Value.AdvertisedHost;
        if (string.IsNullOrWhiteSpace(host)) host = "localhost";
        return Task.FromResult(host);
    }
}
