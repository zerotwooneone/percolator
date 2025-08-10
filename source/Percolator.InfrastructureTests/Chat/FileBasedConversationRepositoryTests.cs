using AutoFixture;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure;
using Percolator.Infrastructure.Chat;

namespace Percolator.InfrastructureTests.Chat;

[TestFixture]
public class FileBasedConversationRepositoryTests
{
    private Fixture _fixture = null!;
    private string _testDirectory = null!;
    private Mock<ISelfParticipantIdProvider> _mockSelfIdProvider = null!;
    private Mock<ILogger<FileBasedConversationRepository>> _mockLogger;

    [SetUp]
    public void Setup()
    {
        _fixture = new Fixture();
        _fixture.Customize<Conversation>(c => c.FromFactory(() =>
        {
            var id = _fixture.Create<ConversationId>();
            var channelId = _fixture.Create<ChannelId>();
            var participants = _fixture.CreateMany<ParticipantId>(2).ToList();
            var messages = new List<Message>();
            var name = _fixture.Create<string>();

            var conversation = new Conversation(id, channelId, participants, messages, name);
            conversation.AddMessage(participants[0], _fixture.Create<string>());
            conversation.AddMessage(participants[1], _fixture.Create<string>());
            return conversation;
        }));

        _testDirectory = Path.Combine(Path.GetTempPath(), "percolator-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testDirectory);

        var selfId = _fixture.Create<ParticipantId>();
        _mockSelfIdProvider = new Mock<ISelfParticipantIdProvider>();
        _mockSelfIdProvider.Setup(p => p.Get()).Returns(selfId);
        _mockLogger = new Mock<ILogger<FileBasedConversationRepository>>();
    }

    [TearDown]
    public void Teardown()
    {
        if (Directory.Exists(_testDirectory))
        {
            try
            {
                Directory.Delete(_testDirectory, true);
            }
            catch
            {
                // Don't fail tests on cleanup errors
            }
        }
    }

    [Test]
    public async Task AddAsync_ThenGetByIdAsync_ShouldReturnEquivalentConversation()
    {
        // Arrange
        var options = CreateStorageOptions();
        var repository = new FileBasedConversationRepository(options, _mockSelfIdProvider.Object, _mockLogger.Object);
        var originalConversation = _fixture.Create<Conversation>();

        // Act
        await repository.AddAsync(originalConversation);
        var loadedConversation = await repository.GetByIdAsync(originalConversation.Id);

        // Assert
        loadedConversation.Should().NotBeNull();
        loadedConversation.Should().BeEquivalentTo(originalConversation);
    }

    [Test]
    public async Task AddAsync_ThenGetByChannelIdAsync_ShouldReturnEquivalentConversation()
    {
        // Arrange
        var options = CreateStorageOptions();
        var repository = new FileBasedConversationRepository(options, _mockSelfIdProvider.Object, _mockLogger.Object);
        var originalConversation = _fixture.Create<Conversation>();

        // Act
        await repository.AddAsync(originalConversation);
        var loadedConversation = await repository.GetByChannelIdAsync(originalConversation.ChannelId);

        // Assert
        loadedConversation.Should().NotBeNull();
        loadedConversation.Should().BeEquivalentTo(originalConversation);
    }

    [Test]
    public async Task GetByChannelIdAsync_WhenIndexIsPreExisting_ShouldReturnConversation()
    {
        // Arrange
        var options = CreateStorageOptions();
        var firstRepository = new FileBasedConversationRepository(options, _mockSelfIdProvider.Object, _mockLogger.Object);
        var originalConversation = _fixture.Create<Conversation>();
        await firstRepository.AddAsync(originalConversation); // This creates the index file

        // Act
        // Create a new repository instance to force it to load the index from the file
        var secondRepository = new FileBasedConversationRepository(options, _mockSelfIdProvider.Object, _mockLogger.Object);
        var loadedConversation = await secondRepository.GetByChannelIdAsync(originalConversation.ChannelId);

        // Assert
        loadedConversation.Should().NotBeNull();
        loadedConversation.Should().BeEquivalentTo(originalConversation);
    }

    [Test]
    public async Task GetByChannelIdAsync_WhenConversationDoesNotExist_ShouldReturnNull()
    {
        // Arrange
        var options = CreateStorageOptions();
        var repository = new FileBasedConversationRepository(options, _mockSelfIdProvider.Object, _mockLogger.Object);
        var randomChannelId = _fixture.Create<ChannelId>();

        // Act
        var result = await repository.GetByChannelIdAsync(randomChannelId);

        // Assert
        result.Should().BeNull();
    }

    private IOptions<StorageOptions> CreateStorageOptions() =>
        Options.Create(new StorageOptions { Path = _testDirectory });
}