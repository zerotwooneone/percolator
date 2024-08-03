using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;

namespace Percolator.Desktop.Main;

public interface IAnnouncerService
{
    bool TryGetIpAddress([NotNullWhen(true)] out IPAddress? localIp);
    Task SendIntroduction(IPAddress destination, int port, IPAddress sourceIp,
        CancellationToken cancellationToken = default);
    Task SendReplyIntroduction(AnnouncerModel announcerModel, IPAddress sourceIp,
        CancellationToken cancellationToken = default);
}