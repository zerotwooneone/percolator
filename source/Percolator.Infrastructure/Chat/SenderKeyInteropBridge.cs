using Microsoft.EntityFrameworkCore;
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

    public bool TryLoadSenderKey(ConversationId conversationId, CryptoPublicIdentity senderPublicIdentityId, DeviceId deviceId, out byte[] recordBytes)
    {
        using var db = _dbFactory.CreateDbContext();
        
        // Resolve local PeerId from CryptoPublicIdentity
        var peerIdentity = db.PeerIdentities.FirstOrDefault(p => p.PublicIdentityId == senderPublicIdentityId.Value);
        if (peerIdentity is null)
        {
            recordBytes = Array.Empty<byte>();
            return false;
        }
        
        var record = db.SenderKeyRecords.Find(conversationId.Value, peerIdentity.PeerId, deviceId.Value);
        
        if (record is null)
        {
            recordBytes = Array.Empty<byte>();
            return false;
        }

        recordBytes = record.RecordBytes;
        return true;
    }

    public void StoreSenderKey(ConversationId conversationId, CryptoPublicIdentity senderPublicIdentityId, DeviceId deviceId, byte[] recordBytes)
    {
        using var db = _dbFactory.CreateDbContext();
        
        // Resolve local PeerId from CryptoPublicIdentity
        var peerIdentity = db.PeerIdentities.FirstOrDefault(p => p.PublicIdentityId == senderPublicIdentityId.Value);
        if (peerIdentity is null)
        {
            throw new InvalidOperationException($"Peer identity not found for PublicIdentityId: {senderPublicIdentityId.Value}");
        }
        
        var existing = db.SenderKeyRecords.Find(conversationId.Value, peerIdentity.PeerId, deviceId.Value);
        
        if (existing is not null)
        {
            existing.RecordBytes = recordBytes;
        }
        else
        {
            var newRecord = new SenderKeyRecordDbo
            {
                ConversationId = conversationId.Value,
                SenderPeerId = peerIdentity.PeerId,
                DeviceId = deviceId.Value,
                RecordBytes = recordBytes
            };
            db.SenderKeyRecords.Add(newRecord);
        }

        db.SaveChanges();
    }
}
