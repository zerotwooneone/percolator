using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public interface IAnnouncerInitializer
{
    Observable<ByteString> AnnouncerAdded { get; }
    IReadOnlyDictionary<ByteString, RemoteClientModel> Announcers { get; }
    void AddKnownAnnouncers(IEnumerable<RemoteClientModel> announcerModels);
}