using AutoFixture;
using Percolator.Chat.ValueObjects;

namespace Percolator.Chat.Tests;

internal class ConversationBuilder
{
    private readonly IFixture _fixture = new Fixture();
    private ConversationId _id;
    private ChannelId _channelId;
    private List<ParticipantId> _participants;
    private List<Message> _messages;
    private string? _name;

    public ConversationBuilder()
    {
        _id = _fixture.Create<ConversationId>();
        _channelId = _fixture.Create<ChannelId>();
        _participants = _fixture.CreateMany<ParticipantId>(2).ToList();
        _messages = new List<Message>();
        _name = null;
    }

    public ConversationBuilder WithId(ConversationId id)
    {
        _id = id;
        return this;
    }

    public ConversationBuilder WithParticipants(params ParticipantId[] participants)
    {
        _participants = participants.ToList();
        return this;
    }

    public ConversationBuilder WithParticipants(IEnumerable<ParticipantId> participants)
    {
        _participants = participants.ToList();
        return this;
    }

    public ConversationBuilder WithMessages(IEnumerable<Message> messages)
    {
        _messages = messages.ToList();
        return this;
    }

    public ConversationBuilder WithName(string? name)
    {
        _name = name;
        return this;
    }

    public Conversation Build()
    {
        return new Conversation(_id, _channelId, _participants, _messages, _name);
    }
}
