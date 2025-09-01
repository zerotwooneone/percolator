using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Percolator.Application.Identity
{
    public interface ISelfIdentityRepository
    {
        Task<SelfIdentityDto?> GetByIdAsync(int id);
        Task<SelfIdentityDto?> GetByPeerIdAsync(Guid peerId);
        Task<SelfIdentityDto?> GetByNameAsync(string name);
        Task<SelfIdentityDto?> GetByNameWithFallbackAsync(string name, string fallbackName);
        Task<IReadOnlyList<SelfIdentityDto>> ListAsync();
        Task<int> CreateAsync(Guid peerId, string name);
        Task<bool> AddKnownPeerAsync(int selfIdentityId, Guid peerId);
        Task<IReadOnlyList<Guid>> ListKnownPeersAsync(int selfIdentityId);
    }
}
