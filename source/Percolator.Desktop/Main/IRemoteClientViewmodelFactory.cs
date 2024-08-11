using Google.Protobuf;
using Percolator.Desktop.Domain.Client;

namespace Percolator.Desktop.Main;

public interface IRemoteClientViewmodelFactory
{
    RemoteClientViewmodel Create(RemoteClientModel remoteClient);
}