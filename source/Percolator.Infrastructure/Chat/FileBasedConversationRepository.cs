using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;
using Percolator.Infrastructure.Serialization;

namespace Percolator.Infrastructure.Chat;

public class FileBasedConversationRepository : IConversationRepository
{
    private readonly StorageOptions _storageOptions;
    private readonly PercolatorJsonContext _jsonContext;

    public FileBasedConversationRepository(IOptions<StorageOptions> storageOptions)
    {
        _storageOptions = storageOptions.Value;
        _jsonContext = new PercolatorJsonContext(new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    public async Task<Conversation?> GetByIdAsync(ConversationId conversationId)
    {
        var path = GetPath(conversationId);
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

    public async Task AddAsync(Conversation conversation)
    {
        var model = ToModel(conversation);
        var path = GetPath(conversation.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(model, _jsonContext.ConversationModel);
        await File.WriteAllTextAsync(path, json);
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
            model.Participants.Select(p => new ParticipantId(p)).ToList(),
            model.Messages.Select(m => new Message(
                new MessageId(m.Id),
                new ParticipantId(m.Sender),
                m.Body,
                m.SentAt)).ToList(),
            model.Name,
            model.AvatarUrl);
    }

    private static ConversationModel ToModel(Conversation conversation)
    {
        return new ConversationModel
        {
            Id = conversation.Id.Value,
            Participants = conversation.Participants.Select(p => p.Value).ToList(),
            Messages = conversation.Messages.Select(m => new MessageModel
            {
                Id = m.Id.Value,
                Sender = m.SenderId.Value,
                SentAt = m.Timestamp,
                Body = m.Content
            }).ToList(),
            Name = conversation.Name,
            AvatarUrl = conversation.AvatarUrl
        };
    }

    private string GetPath(ConversationId conversationId) =>
        Path.Combine(_storageOptions.Path, "conversations", $"{conversationId.Value}.json");
}
