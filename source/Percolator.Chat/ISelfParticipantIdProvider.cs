using Percolator.Chat.Messaging.ValueObjects;

namespace Percolator.Chat;

public interface ISelfParticipantIdProvider
{
    ParticipantId Get();
}