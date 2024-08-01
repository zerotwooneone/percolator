using System.Net;
using System.Security.Cryptography;

namespace Percolator.Desktop.Main;

public interface IAnnouncerService
{
    Task SendIntroduction(IPAddress destination, int port, CancellationToken cancellationToken = default);
    Task SendReplyIntroduction(IPAddress ipAddress, int port, RSACryptoServiceProvider ephemeral);
}