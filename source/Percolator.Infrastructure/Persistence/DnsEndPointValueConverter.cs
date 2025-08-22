using System.Net;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Percolator.Infrastructure.Persistence;

public class DnsEndPointValueConverter : ValueConverter<DnsEndPoint, string>
{
    public DnsEndPointValueConverter() : base(
        v => v.ToString(),
        v => Parse(v))
    {
    }

    private static DnsEndPoint Parse(string endpointString)
    {
        if (string.IsNullOrWhiteSpace(endpointString))
        {
            throw new ArgumentException("Endpoint string cannot be empty.", nameof(endpointString));
        }

        var lastColonIndex = endpointString.LastIndexOf(':');
        if (lastColonIndex == -1)
        {
            throw new FormatException("Invalid endpoint format. Port is missing.");
        }

        var hostPart = endpointString.Substring(0, lastColonIndex);
        if (hostPart.StartsWith("[") && hostPart.EndsWith("]"))
        {
            hostPart = hostPart.Substring(1, hostPart.Length - 2);
        }

        var portPart = endpointString.Substring(lastColonIndex + 1);
        if (!int.TryParse(portPart, out var port))
        {
            throw new FormatException("Invalid port number.");
        }

        return new DnsEndPoint(hostPart, port);
    }
}
