using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Serialization;

namespace Percolator.Infrastructure.Chat;

public class FileBasedConversationRepository : IConversationRepository
{
    private readonly ISelfParticipantIdProvider _selfParticipantIdProvider;
    private readonly StorageOptions _storageOptions;
    private readonly PercolatorJsonContext _jsonContext;
    private readonly ConcurrentDictionary<string, Guid> _channelIdIndex;


    public FileBasedConversationRepository(
        IOptions<StorageOptions> storageOptions,
        ISelfParticipantIdProvider selfParticipantIdProvider)
    {
        _selfParticipantIdProvider = selfParticipantIdProvider;
        _storageOptions = storageOptions.Value;
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true
        });

        _channelIdIndex = LoadIndex();
    }

    private ConcurrentDictionary<string, Guid> LoadIndex()
    {
        Directory.CreateDirectory(GetConversationsPath());
        var channelIndexPath = GetChannelIndexPath();
        if (!File.Exists(channelIndexPath))
        {
            return new ConcurrentDictionary<string, Guid>();
        }

        var json = File.ReadAllText(channelIndexPath);
        var index = JsonSerializer.Deserialize<Dictionary<string, Guid>>(json);
        return new ConcurrentDictionary<string, Guid>(index ?? new Dictionary<string, Guid>());
    }

    private async Task PersistIndex()
    {
        var json = JsonSerializer.Serialize(_channelIdIndex, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(GetConversationsPath());
        await File.WriteAllTextAsync(GetChannelIndexPath(), json);
    }

    public async Task<Conversation?> GetByIdAsync(ConversationId conversationId)
    {
        Directory.CreateDirectory(GetConversationsPath());
        var path = GetConversationPath(conversationId);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path);
        var model = JsonSerializer.Deserialize(json, _jsonContext.ConversationModel);

        if (model is null)
            return null;

        return ToDomain(model);
    }

    public async Task<Conversation?> GetByChannelIdAsync(ChannelId id)
    {
        var channelIdKey = Convert.ToBase64String(id.Value);
        if (_channelIdIndex.TryGetValue(channelIdKey, out var conversationIdGuid))
        {
            return await GetByIdAsync(new ConversationId(conversationIdGuid));
        }

        return null;
    }

    public async Task AddAsync(Conversation conversation)
    {
        var model = ToModel(conversation);
        var path = GetConversationPath(conversation.Id);
        Directory.CreateDirectory(GetConversationsPath());
        var json = JsonSerializer.Serialize(model, _jsonContext.ConversationModel);
        await File.WriteAllTextAsync(path, json);

        // Update and persist the index
        var channelIdKey = Convert.ToBase64String(conversation.ChannelId.Value);
        _channelIdIndex[channelIdKey] = conversation.Id.Value;
        await PersistIndex();
    }

    public Task UpdateAsync(Conversation conversation)
    {
        // For file-based repo, Add and Update can be the same.
        return AddAsync(conversation);
    }

    private static Conversation ToDomain(ConversationModel model)
    {
        return new Conversation(
            new ConversationId(model.Id),
            new ChannelId(model.ChannelId),
            model.Participants.Select(p => new ParticipantId(p)).ToList(),
            model.Messages.Select(m => new Message(
                new MessageId(m.Id),
                new ParticipantId(m.Sender),
                m.Body,
                m.SentAt)).ToList(),
            model.Name);
    }

    private static ConversationModel ToModel(Conversation conversation)
    {
        return new ConversationModel
        {
            Id = conversation.Id.Value,
            ChannelId = conversation.ChannelId.Value,
            Participants = conversation.Participants.Select(p => p.Value).ToList(),
            Messages = conversation.Messages.Select(m => new MessageModel
            {
                Id = m.Id.Value,
                Sender = m.SenderId.Value,
                SentAt = m.Timestamp,
                Body = m.Content
            }).ToList(),
            Name = conversation.Name
        };
    }

    private string GetConversationsPath() =>
        Path.Combine(_storageOptions.Path,_selfParticipantIdProvider.Get().Value.ToString(), "conversations");
    private string GetConversationPath(ConversationId conversationId) =>
        Path.Combine(GetConversationsPath(), $"{conversationId.Value}.json");
    private string GetChannelIndexPath() =>
        Path.Combine(GetConversationsPath(), "channel_id_index.json");
}
