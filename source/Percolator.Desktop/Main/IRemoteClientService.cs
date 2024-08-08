using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Percolator.Desktop.Main;

public interface IRemoteClientService
{
    bool TryGetIpAddress([NotNullWhen(true)] out IPAddress? localIp);
    Task SendIntroduction(IPAddress destination, int port, IPAddress sourceIp,
        CancellationToken cancellationToken = default);
    Task SendReplyIntroduction(RemoteClientModel remoteClientModel, IPAddress sourceIp,
        CancellationToken cancellationToken = default);
}