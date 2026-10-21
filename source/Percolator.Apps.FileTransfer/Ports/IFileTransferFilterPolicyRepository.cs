using Percolator.Apps.FileTransfer.Features.IngressControl;
using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Ports;

public interface IFileTransferFilterPolicyRepository
{
    ValueTask SavePolicyAsync(FileTransferFilterPolicy policy, CancellationToken ct = default);
    ValueTask<FileTransferFilterPolicy> GetPolicyAsync(PublicIdentityId ownerId, CancellationToken ct = default);
}
