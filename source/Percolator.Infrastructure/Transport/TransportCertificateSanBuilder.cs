namespace Percolator.Infrastructure.Transport;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

public static class TransportCertificateSanBuilder
{
    public static void PopulateSan(SubjectAlternativeNameBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var addedIps = new HashSet<IPAddress> { IPAddress.Loopback, IPAddress.IPv6Loopback };
        var addedDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "localhost" };

        builder.AddDnsName("localhost");
        builder.AddIpAddress(IPAddress.Loopback);
        builder.AddIpAddress(IPAddress.IPv6Loopback);

        try
        {
            var hostName = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName) &&
                Uri.CheckHostName(hostName) == UriHostNameType.Dns &&
                addedDns.Add(hostName))
            {
                builder.AddDnsName(hostName);
            }

            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                try
                {
                    if (ni.OperationalStatus != OperationalStatus.Up ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                    {
                        continue;
                    }

                    foreach (var uni in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (uni.Address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                        {
                            var cleanAddress = uni.Address.AddressFamily == AddressFamily.InterNetworkV6 && uni.Address.ScopeId != 0
                                ? new IPAddress(uni.Address.GetAddressBytes())
                                : uni.Address;

                            if (addedIps.Add(cleanAddress))
                            {
                                builder.AddIpAddress(cleanAddress);
                            }
                        }
                    }
                }
                catch (Exception)
                {
                    // Fault isolation: skip malfunctioning virtual/VPN adapters and continue enumerating remaining interfaces
                }
            }
        }
        catch (Exception)
        {
            // Best-effort network enumeration fallback - loopback and localhost are always guaranteed above
        }
    }
}
