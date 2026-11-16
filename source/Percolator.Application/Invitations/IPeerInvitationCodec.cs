namespace Percolator.Application.Invitations;

using Percolator.Domain.Common;

public interface IPeerInvitationCodec
{
    string Encode(PeerInvitationPayload payload);
    DomainResult<PeerInvitationPayload> Decode(string invitationString);
}
