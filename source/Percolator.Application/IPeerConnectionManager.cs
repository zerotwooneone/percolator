using Grpc.Net.Client;
using Percolator.Contracts.Protos;
using Percolator.Network;

namespace Percolator.Application;

public interface IPeerConnectionManager
{
    FileSharing.FileSharingClient GetClient(Peer peer);
    void RemovePeer(Peer peer);
}
