using System.Diagnostics.CodeAnalysis;
using System.Net;
using Percolator.Desktop.Domain.Client;

namespace Percolator.Desktop.Main;

public interface IChatService
{
    Task SendChatMessage(RemoteClientModel remoteClientModel, string text,
        CancellationToken cancellationToken = default);
    
    bool TryGetIpAddress([NotNullWhen(true)] out IPAddress? localIp);
        Task SendIntroduction(IPAddress destination, int port, IPAddress sourceIp,
            CancellationToken cancellationToken = default);
        Task SendReplyIntroduction(RemoteClientModel remoteClientModel, IPAddress sourceIp,
            CancellationToken cancellationToken = default);
}