using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Percolator.Desktop.Main;

public interface IChatService
{
    Task SendChatMessage(AnnouncerModel announcerModel, string text,
        CancellationToken cancellationToken = default);
    
    bool TryGetIpAddress([NotNullWhen(true)] out IPAddress? localIp);
        Task SendIntroduction(IPAddress destination, int port, IPAddress sourceIp,
            CancellationToken cancellationToken = default);
        Task SendReplyIntroduction(AnnouncerModel announcerModel, IPAddress sourceIp,
            CancellationToken cancellationToken = default);
}