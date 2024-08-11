namespace Percolator.Desktop.Domain.Client;

public interface IRemoteClientInitializer
{
    void AddKnownAnnouncers(IEnumerable<RemoteClientModel> announcerModels);
}