using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public interface IRemoteClientInitializer
{
    void AddKnownAnnouncers(IEnumerable<RemoteClientModel> announcerModels);
}