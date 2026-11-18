using System;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Percolator.Simulator.Features.Topology;

internal static class SimulatorNetworkHelper
{
    public static string DetectLanIp()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                foreach (var uni in ni.GetIPProperties().UnicastAddresses)
                {
                    if (uni.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(uni.Address))
                    {
                        return uni.Address.ToString();
                    }
                }
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException)
        {
            // Transient network interface enumeration failure; fall back to loopback.
        }

        return "127.0.0.1";
    }
}
