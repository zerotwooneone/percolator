using Google.Protobuf;
using R3;

namespace Percolator.Desktop.Main;

public interface IRemoteClientRepository
{
    Observable<ByteString> ClientAdded { get; }
    RemoteClientModel? GetClientByIdentity(ByteString identity);
    RemoteClientModel GetOrAdd(ByteString identity, Func<ByteString, RemoteClientModel> addCallback);
    void OnNext(ByteString identity);
    IDisposable WatchForChanges(RemoteClientModel remoteClient);
}