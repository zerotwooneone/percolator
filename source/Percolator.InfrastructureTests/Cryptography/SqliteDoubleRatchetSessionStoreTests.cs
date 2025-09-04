using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Cryptography;
using Percolator.Infrastructure.Cryptography;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Cryptography;

[TestFixture]
public class SqliteDoubleRatchetSessionStoreTests
{
    private static PercolatorDbContext CreateDbContext(out SqliteConnection connection)
    {
        connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        var options = new DbContextOptionsBuilder<PercolatorDbContext>()
            .UseSqlite(connection)
            .Options;
        var ctx = new PercolatorDbContext(options);
        ctx.Database.EnsureCreated();
        // Seed a SelfIdentity required by DoubleRatchetSessions FK
        ctx.SelfIdentities.Add(new SelfIdentityDbo { Name = "test", PeerId = Guid.NewGuid() });
        ctx.SaveChanges();
        return ctx;
    }

    private static DoubleRatchetSession.DoubleRatchetSessionState CreateTestSessionState()
    {
        byte[] Make(int len)
        {
            var b = new byte[len];
            RandomNumberGenerator.Fill(b);
            return b;
        }

        var rootKey = Make(32);
        var sendKey = Make(32);
        var recvKey = Make(32);
        var idPub = Make(32);
        var dhPub = Make(91); // typical SPKI length for P-256
        var dhPriv = Make(121); // DER ECPrivateKey length varies; arbitrary for test
        var msgKey = Make(32);

        var state = new DoubleRatchetSession.DoubleRatchetSessionState
        {
            RootKey = new RootKey(rootKey),
            RatchetFlag = true,
            SendingChainKey = new ChainKey(sendKey),
            ReceivingChainKey = new ChainKey(recvKey),
            SendingCounter = 7,
            ReceivingCounter = 3,
            PreviousChainLength = 42,
            TheirIdentityPublicKey = new RatchetIdentityKey(idPub),
            TheirDhRatchetPublicKey = new PreKey(dhPub),
            DhRatchetPrivateKey = new PrivateEphemeralKey(dhPriv)
        };
        state.SkippedMessageKeys[new SkippedMessageKeyIdentifier(new PreKey(dhPub), 5)] = msgKey;
        return state;
    }

    [Test]
    public async Task Set_and_Get_roundtrips_state()
    {
        var ctx = CreateDbContext(out var _);
        var store = new SqliteDoubleRatchetSessionStore(ctx, NullLogger<SqliteDoubleRatchetSessionStore>.Instance);
        var sessionId = SessionId.NewId();
        var state = CreateTestSessionState();
        var selfIdentityId = await ctx.SelfIdentities.Select(s => s.Id).FirstAsync();

        await store.SetSessionStateAsync(sessionId, state, selfIdentityId);
        var loaded = await store.GetSessionStateAsync(sessionId, selfIdentityId);

        loaded.Should().NotBeNull();
        loaded!.RootKey!.Value.Should().BeEquivalentTo(state.RootKey!.Value);
        (loaded.SendingChainKey?.Value).Should().BeEquivalentTo(state.SendingChainKey!.Value);
        (loaded.ReceivingChainKey?.Value).Should().BeEquivalentTo(state.ReceivingChainKey!.Value);
        loaded.SendingCounter.Should().Be(state.SendingCounter);
        loaded.ReceivingCounter.Should().Be(state.ReceivingCounter);
        loaded.PreviousChainLength.Should().Be(state.PreviousChainLength);
        loaded.RatchetFlag.Should().Be(state.RatchetFlag);
        loaded.TheirIdentityPublicKey!.Value.Should().BeEquivalentTo(state.TheirIdentityPublicKey!.Value);
        loaded.TheirDhRatchetPublicKey!.Value.Should().BeEquivalentTo(state.TheirDhRatchetPublicKey!.Value);
        loaded.DhRatchetPrivateKey!.Value.Should().BeEquivalentTo(state.DhRatchetPrivateKey!.Value);
        loaded.SkippedMessageKeys.Count.Should().Be(state.SkippedMessageKeys.Count);
        foreach (var kv in state.SkippedMessageKeys)
        {
            loaded.SkippedMessageKeys.Should().ContainKey(kv.Key);
            loaded.SkippedMessageKeys[kv.Key].Should().BeEquivalentTo(kv.Value);
        }
    }

    [Test]
    public async Task Set_overwrites_state_for_same_session()
    {
        var ctx = CreateDbContext(out var _);
        var store = new SqliteDoubleRatchetSessionStore(ctx, NullLogger<SqliteDoubleRatchetSessionStore>.Instance);
        var sessionId = SessionId.NewId();
        var selfIdentityId = await ctx.SelfIdentities.Select(s => s.Id).FirstAsync();

        var s1 = CreateTestSessionState();
        await store.SetSessionStateAsync(sessionId, s1, selfIdentityId);

        var s2 = CreateTestSessionState();
        await store.SetSessionStateAsync(sessionId, s2, selfIdentityId);

        var loaded = await store.GetSessionStateAsync(sessionId, selfIdentityId);
        loaded!.RootKey!.Value.Should().BeEquivalentTo(s2.RootKey!.Value);
    }
}
