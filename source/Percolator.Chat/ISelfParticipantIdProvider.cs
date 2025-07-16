using Percolator.Chat.ValueObjects;

namespace Percolator.Chat;

public interface ISelfParticipantIdProvider
{
    ParticipantId Get();
}