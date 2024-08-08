using Google.Protobuf;

namespace Percolator.Desktop.Main;

public interface IRemoteClientViewmodelFactory
{
    RemoteClientViewmodel Create(RemoteClientModel remoteClient);
}