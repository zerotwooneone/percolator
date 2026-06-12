using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;
using Percolator.Infrastructure.Chat.Persistence;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Chat;

public sealed class SenderKeyInteropBridge : ISenderKeyInteropBridge
{
    private readonly IDbContextFactory<PercolatorDbContext> _dbFactory;

    public SenderKeyInteropBridge(IDbContextFactory<PercolatorDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public bool TryLoadSenderKey(ConversationId conversationId, PeerId senderId, DeviceId deviceId, out byte[] recordBytes)
    {
        using var db = _dbFactory.CreateDbContext();
        var record = db.SenderKeyRecords.Find(conversationId.Value, senderId.Value, deviceId.Value);
        
        if (record is null)
        {
            recordBytes = Array.Empty<byte>();
            return false;
        }

        recordBytes = record.RecordBytes;
        return true;
    }

    public void StoreSenderKey(ConversationId conversationId, PeerId senderId, DeviceId deviceId, byte[] recordBytes)
    {
        using var db = _dbFactory.CreateDbContext();
        var existing = db.SenderKeyRecords.Find(conversationId.Value, senderId.Value, deviceId.Value);
        
        if (existing is not null)
        {
            existing.RecordBytes = recordBytes;
        }
        else
        {
            var newRecord = new SenderKeyRecordDbo
            {
                ConversationId = conversationId.Value,
                SenderPeerId = senderId.Value,
                DeviceId = deviceId.Value,
                RecordBytes = recordBytes
            };
            db.SenderKeyRecords.Add(newRecord);
        }

        db.SaveChanges();
    }
}
