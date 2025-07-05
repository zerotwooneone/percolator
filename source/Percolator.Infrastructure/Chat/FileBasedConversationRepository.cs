using System.Text.Json;
using Microsoft.Extensions.Options;
using Percolator.Chat;
using Percolator.Chat.ValueObjects;

namespace Percolator.Infrastructure.Chat;

public class FileBasedConversationRepository : IConversationRepository
{
    private readonly StorageOptions _storageOptions;

    public FileBasedConversationRepository(IOptions<StorageOptions> storageOptions)
    {
        _storageOptions = storageOptions.Value;
    }

    public async Task<Conversation?> GetByIdAsync(ConversationId id)
    {
        var path = GetPath(id);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path);
        return JsonSerializer.Deserialize<Conversation>(json);
    }

    public async Task AddAsync(Conversation conversation)
    {
        var path = GetPath(conversation.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(conversation);
        await File.WriteAllTextAsync(path, json);
    }

    public async Task UpdateAsync(Conversation conversation)
    {
        // For file-based, Add and Update are the same
        await AddAsync(conversation);
    }

    private string GetPath(ConversationId id) =>
        Path.Combine(_storageOptions.Path, "conversations", $"{id.Value}.json");
}
