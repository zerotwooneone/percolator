using Percolator.Chat.ValueObjects;
using Percolator.Network;
using PeerId = Percolator.Identity.PeerId;

namespace Percolator.Application.Sessions;

public interface IMessageService
{
    Task SendDirectMessageAsync(
        DirectSessionId directSessionId, 
        string content,
        PeerId remotePeerId);
}
