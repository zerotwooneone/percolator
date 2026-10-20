using Percolator.Domain.Identities.ValueObjects;

namespace Percolator.Apps.FileTransfer.Domain.TransferNegotiation;

public abstract record TransferTopology
{
    public PublicIdentityId InitiatorId { get; }

    protected TransferTopology(PublicIdentityId initiatorId)
    {
        InitiatorId = initiatorId;
    }

    public abstract bool ContainsParticipant(PublicIdentityId peerId);

    public sealed record OneToOne : TransferTopology
    {
        public PublicIdentityId RecipientId { get; }

        public OneToOne(PublicIdentityId initiatorId, PublicIdentityId recipientId) 
            : base(initiatorId)
        {
            RecipientId = recipientId;
        }

        public override bool ContainsParticipant(PublicIdentityId peerId) =>
            peerId == InitiatorId || peerId == RecipientId;
    }

    public sealed record MultiPeer : TransferTopology
    {
        public IReadOnlyList<PublicIdentityId> Recipients { get; }

        public MultiPeer(PublicIdentityId initiatorId, IReadOnlyList<PublicIdentityId> recipients) 
            : base(initiatorId)
        {
            Recipients = recipients;
        }

        public override bool ContainsParticipant(PublicIdentityId peerId)
        {
            if (peerId == InitiatorId) return true;
            for (int i = 0; i < Recipients.Count; i++)
            {
                if (Recipients[i] == peerId) return true;
            }
            return false;
        }
    }

    public sealed record IncomingStaged : TransferTopology
    {
        public IncomingStaged(PublicIdentityId initiatorId) 
            : base(initiatorId)
        {
        }

        public override bool ContainsParticipant(PublicIdentityId peerId) =>
            peerId == InitiatorId;
    }
}
