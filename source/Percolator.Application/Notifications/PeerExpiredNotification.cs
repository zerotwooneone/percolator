using MediatR;
using Percolator.Network;

namespace Percolator.Application.Notifications
{
    public class PeerExpiredNotification : INotification
    {
        public Peer Peer { get; }

        public PeerExpiredNotification(Peer peer)
        {
            Peer = peer;
        }
    }
}
