using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Percolator.Cryptography;
using Percolator.Infrastructure.Persistence;

namespace Percolator.Infrastructure.Cryptography;

public sealed class SqliteDoubleRatchetSessionStore : IDoubleRatchetSessionStore
{
    private readonly PercolatorDbContext _db;
    private readonly ILogger<SqliteDoubleRatchetSessionStore> _logger;

    public SqliteDoubleRatchetSessionStore(PercolatorDbContext db, ILogger<SqliteDoubleRatchetSessionStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<DoubleRatchetSession.DoubleRatchetSessionState?> GetSessionStateAsync(SessionId sessionId, int selfIdentityId)
    {
        var dbo = await _db.DoubleRatchetSessions
            .AsNoTracking()
            .Include(s => s.SkippedMessageKeys)
            .FirstOrDefaultAsync(s => s.SessionId == sessionId.Value && s.SelfIdentityId == selfIdentityId);

        if (dbo is null)
        {
            _logger.LogDebug("No DoubleRatchet session found for {SessionId}", sessionId);
            return null;
        }

        var state = new DoubleRatchetSession.DoubleRatchetSessionState
        {
            RootKey = new RootKey(dbo.RootKey),
            RatchetFlag = dbo.RatchetFlag,
            SendingChainKey = dbo.SendingChainKey is null ? null : new ChainKey(dbo.SendingChainKey),
            ReceivingChainKey = dbo.ReceivingChainKey is null ? null : new ChainKey(dbo.ReceivingChainKey),
            SendingCounter = dbo.SendingCounter,
            ReceivingCounter = dbo.ReceivingCounter,
            PreviousChainLength = dbo.PreviousChainLength,
            TheirDhRatchetPublicKey = dbo.TheirDhRatchetPublicKey is null ? null : new PreKey(dbo.TheirDhRatchetPublicKey),
            DhRatchetPrivateKey = dbo.DhRatchetPrivateKey is null ? null : new PrivateEphemeralKey(dbo.DhRatchetPrivateKey),
            TheirIdentityPublicKey = new RatchetIdentityKey(dbo.TheirIdentityPublicKey)
        };

        // Rehydrate skipped message keys
        foreach (var k in dbo.SkippedMessageKeys)
        {
            var id = new SkippedMessageKeyIdentifier(new PreKey(k.RatchetKey), k.MessageNumber);
            state.SkippedMessageKeys[id] = k.MessageKey;
        }

        return state;
    }

    public async Task SetSessionStateAsync(SessionId sessionId, DoubleRatchetSession.DoubleRatchetSessionState sessionState, int selfIdentityId)
    {
        var dbo = await _db.DoubleRatchetSessions
            .Include(s => s.SkippedMessageKeys)
            .FirstOrDefaultAsync(s => s.SessionId == sessionId.Value && s.SelfIdentityId == selfIdentityId);

        if (dbo is null)
        {
            dbo = new DoubleRatchetSessionDbo
            {
                SessionId = sessionId.Value,
                SelfIdentityId = selfIdentityId
            };
            _db.DoubleRatchetSessions.Add(dbo);
        }

        dbo.SelfIdentityId = selfIdentityId;
        dbo.RootKey = sessionState.RootKey!.Value;
        dbo.RatchetFlag = sessionState.RatchetFlag;
        dbo.SendingChainKey = sessionState.SendingChainKey?.Value;
        dbo.ReceivingChainKey = sessionState.ReceivingChainKey?.Value;
        dbo.SendingCounter = sessionState.SendingCounter;
        dbo.ReceivingCounter = sessionState.ReceivingCounter;
        dbo.PreviousChainLength = sessionState.PreviousChainLength;
        dbo.TheirDhRatchetPublicKey = sessionState.TheirDhRatchetPublicKey?.Value;
        dbo.DhRatchetPrivateKey = sessionState.DhRatchetPrivateKey?.Value;
        dbo.TheirIdentityPublicKey = sessionState.TheirIdentityPublicKey!.Value;
        dbo.UpdatedAt = DateTimeOffset.UtcNow;

        // Overwrite skipped message keys: clear then add current
        dbo.SkippedMessageKeys.Clear();
        foreach (var kvp in sessionState.SkippedMessageKeys)
        {
            var identifier = kvp.Key;
            dbo.SkippedMessageKeys.Add(new SkippedMessageKeyDbo
            {
                SessionId = sessionId.Value,
                SelfIdentityId = selfIdentityId,
                RatchetKey = identifier.RatchetKey.Value,
                MessageNumber = identifier.MessageNumber,
                MessageKey = kvp.Value
            });
        }

        await _db.SaveChangesAsync();
    }
}
