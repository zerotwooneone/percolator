using Percolator.Desktop.Domain.Chat;
using Percolator.Desktop.Domain.Client;

namespace Percolator.Desktop.Main;

public interface IChatViewmodelFactory
{
    ChatViewmodel CreateChat(RemoteClientModel remoteClientModel);
}