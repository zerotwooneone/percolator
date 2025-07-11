using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Percolator.Chat;
using Percolator.Infrastructure.Chat;

namespace Percolator.InfrastructureTests.Chat;

[TestFixture]
public class FileBasedConversationRepositoryTests
{
    private Fixture _fixture = null!;
    private string _testDirectory = null!;

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
        _fixture.Customize<Conversation>(c => c.FromFactory(() =>
        {
            var id = _fixture.Create<Percolator.Chat.ValueObjects.ConversationId>();
            var channelId = _fixture.Create<Percolator.Chat.ValueObjects.ChannelId>();
            var participants = _fixture.CreateMany<Percolator.Chat.ValueObjects.ParticipantId>(2).ToList();
            var messages = new List<Percolator.Chat.Message>();
            var name = _fixture.Create<string>();

            var conversation = new Conversation(id, channelId, participants, messages, name);
            conversation.AddMessage(participants[0], _fixture.Create<string>());
            conversation.AddMessage(participants[1], _fixture.Create<string>());
            return conversation;
        }));

        _testDirectory = Path.Combine(Path.GetTempPath(), "percolator-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);
    }

    [TearDown]
    public void Teardown()
    {
        if (Directory.Exists(_testDirectory))
        {
            Directory.Delete(_testDirectory, true);
        }
    }

    [Test]
    public async Task AddAsync_ThenGetByIdAsync_ShouldReturnEquivalentConversation()
    {
        // Arrange
        var storageOptions = new Percolator.Infrastructure.StorageOptions { Path = _testDirectory };
        var options = Options.Create(storageOptions);
        var repository = new FileBasedConversationRepository(options);
        var originalConversation = _fixture.Create<Conversation>();

        // Act
        await repository.AddAsync(originalConversation);
        var loadedConversation = await repository.GetByIdAsync(originalConversation.Id);

        // Assert
        loadedConversation.Should().NotBeNull();
        loadedConversation.Should().BeEquivalentTo(originalConversation);
    }
}