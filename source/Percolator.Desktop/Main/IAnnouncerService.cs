using System.Net;

namespace Percolator.Desktop.Main;

public interface IAnnouncerService
{
    Task SendIntroduction(IPAddress destination, int port, CancellationToken cancellationToken = default);
}