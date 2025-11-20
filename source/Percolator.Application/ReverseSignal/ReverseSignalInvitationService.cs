using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.Application.ReverseSignal
{
    public class ReverseSignalInvitationService
    {
        private readonly IPendingSessionRepository _repository;
        private readonly IClock _clock;

        public ReverseSignalInvitationService(IPendingSessionRepository repository, IClock clock)
        {
            _repository = repository;
            _clock = clock;
        }

        public async Task<PendingSessionId> CreatePendingAsync(
            PeerId remotePeerId,
            ProtocolVersion protocolVersion,
            HandshakeInvitation invitation,
            TimeSpan ttl,
            CancellationToken cancellationToken = default)
        {
            var id = PendingSessionId.NewId();
            var expires = _clock.UtcNow.Add(ttl);
            var pending = PendingSession.FromInvitation(
                id,
                remotePeerId,
                protocolVersion,
                invitation,
                _clock,
                expires);

            await _repository.AddAsync(pending, cancellationToken).ConfigureAwait(false);
            return id;
        }
    }
}
