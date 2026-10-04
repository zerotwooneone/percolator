using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.IntegrationTests.Builders;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests;

[TestFixture]
public sealed class GroupMutationScenarioTests
{
    private ScenarioCryptoEngine _cryptoEngine = null!;
    private ScenarioZkProofEngine _zkEngine = null!;
    private InMemoryGroupCredentialsRepository _credentialsRepo = null!;
    private FixedTimeProvider _timeProvider = null!;

    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 3, 15, 0, 0, TimeSpan.Zero);
    }

    [SetUp]
    public void SetUp()
    {
        _cryptoEngine = new ScenarioCryptoEngine();
        _zkEngine = new ScenarioZkProofEngine();
        _credentialsRepo = new InMemoryGroupCredentialsRepository();
        _timeProvider = new FixedTimeProvider();
    }

    [Test]
    public async Task GroupRebase_ConcurrentEpochMutationConflict_ResolvesViaRebase()
    {
        // -------------------------------------------------------------------------
        // 1. SETUP: Alice and Bob are co-admins of "Strategy HQ"
        // -------------------------------------------------------------------------
        using var alice = ScenarioParticipant.Create("Alice", _cryptoEngine);
        using var bob = ScenarioParticipant.Create("Bob", _cryptoEngine);
        using var charlie = ScenarioParticipant.Create("Charlie", _cryptoEngine);

        var context = await ScenarioGroupBuilder
            .Create("Strategy HQ", alice, _cryptoEngine, _zkEngine, _timeProvider)
            .WithMember(bob)
            .BuildAsync(_credentialsRepo);

        using (context)
        {
            var channel = context.Channel;
            var ledger = context.Ledger;
            var credentials = context.Credentials;

            // Promote Bob to co-admin so Bob also has authorization to mutate
            channel.ChangeMemberRole(alice.IdentityId, bob.IdentityId, ChannelRole.Admin, _timeProvider).IsSuccess.Should().BeTrue();

            // Bob maintains a local replica of the GroupChannel
            var bobChannelResult = GroupChannel.CreateGenesis(channel.Id, alice.IdentityId, "Strategy HQ", _timeProvider);
            var bobChannel = bobChannelResult.Value!;
            bobChannel.AddMember(alice.IdentityId, bob.IdentityId, ChannelRole.Admin, _timeProvider).IsSuccess.Should().BeTrue();

            // Both start synchronized at Genesis (Epoch 0)
            ledger.CurrentEpoch.Value.Should().Be(0);
            channel.CurrentEpoch.Value.Should().Be(2); // Alice channel: Genesis(0) -> AddedBob(1) -> PromotedBob(2)

            // Rebase ledger to Epoch 0 baseline tokens for Alice and Bob
            var tokenAlice = BlindedRoutingToken.New();
            var tokenBob = BlindedRoutingToken.New();
            var initialTokens = new HashSet<BlindedRoutingToken> { tokenAlice, tokenBob };

            // -------------------------------------------------------------------------
            // 2. CONCURRENT MUTATION ATTEMPT:
            //    Alice adds Charlie (Epoch 0 -> Epoch 1)
            //    Bob simultaneously renames the room to "War Room" (Epoch 0 -> Epoch 1)
            // -------------------------------------------------------------------------
            var tokenCharlie = BlindedRoutingToken.New();
            var aliceNewTokens = new HashSet<BlindedRoutingToken> { tokenAlice, tokenBob, tokenCharlie };
            var aliceNewBlob = EncryptedEntriesBlob.FromSpan(new byte[160]);

            // Alice generates mutation challenge and proof based on ledger Epoch 0
            var aliceChallenge = ComputeChallenge(aliceNewBlob, aliceNewTokens);
            var aliceProof = _zkEngine.GenerateGroupPresentation(
                ledger.CurrentEpoch.Value,
                aliceChallenge,
                credentials.MasterKey.Span,
                credentials.AuthCredentialMac.Span);

            // Alice submits to Relay Ledger -> Committed! CurrentEpoch becomes 1.
            var aliceCommit = ledger.CommitMutation(
                EpochNumber.Genesis,
                aliceNewBlob,
                aliceNewTokens,
                aliceProof,
                _zkEngine,
                _timeProvider);

            aliceCommit.IsSuccess.Should().BeTrue();
            ledger.CurrentEpoch.Value.Should().Be(1);

            // -------------------------------------------------------------------------
            // 3. BOB'S MUTATION ENCOUNTERS EPOCH_CONFLICT
            // -------------------------------------------------------------------------
            var bobNewBlob = EncryptedEntriesBlob.FromSpan(new byte[140]);
            var bobTokens = new HashSet<BlindedRoutingToken> { tokenAlice, tokenBob };
            var bobChallenge = ComputeChallenge(bobNewBlob, bobTokens);

            // Bob generates proof based on his outdated knowledge of Epoch 0
            var bobProof = _zkEngine.GenerateGroupPresentation(
                epoch: 0,
                bobChallenge,
                credentials.MasterKey.Span,
                credentials.AuthCredentialMac.Span);

            // Bob submits against baseEpoch = 0
            var bobCommit = ledger.CommitMutation(
                EpochNumber.Genesis,
                bobNewBlob,
                bobTokens,
                bobProof,
                _zkEngine,
                _timeProvider);

            bobCommit.IsFailure.Should().BeTrue();
            bobCommit.Error.Code.Should().Be("EPOCH_CONFLICT");
            bobCommit.Error.Description.Should().Contain("Rebase required");

            // -------------------------------------------------------------------------
            // 4. BOB RESOLVES CONFLICT VIA REBASE AND RETRIES AGAINST EPOCH 1
            // -------------------------------------------------------------------------
            // Bob fetches current members and current epoch (1) from relay ledger event
            var updatedMembers = new List<ChannelMember>
            {
                new(alice.IdentityId, ChannelRole.Admin, _timeProvider.UtcNow),
                new(bob.IdentityId, ChannelRole.Admin, _timeProvider.UtcNow),
                new(charlie.IdentityId, ChannelRole.Member, _timeProvider.UtcNow)
            };

            bobChannel.Rebase(ledger.CurrentEpoch, updatedMembers, _timeProvider);
            bobChannel.CurrentEpoch.Value.Should().Be(1);
            bobChannel.Members.Should().HaveCount(3);

            // Bob reapplies his rename operation on top of Epoch 1
            bobChannel.RenameChannel(bob.IdentityId, "War Room", _timeProvider).IsSuccess.Should().BeTrue();
            bobChannel.Name.Should().Be("War Room");

            // Bob generates a new proof matching Epoch 1 and the updated 3-member roster
            var finalTokens = new HashSet<BlindedRoutingToken> { tokenAlice, tokenBob, tokenCharlie };
            var finalBlob = EncryptedEntriesBlob.FromSpan(new byte[180]);
            var finalChallenge = ComputeChallenge(finalBlob, finalTokens);

            var retryProof = _zkEngine.GenerateGroupPresentation(
                ledger.CurrentEpoch.Value,
                finalChallenge,
                credentials.MasterKey.Span,
                credentials.AuthCredentialMac.Span);

            var retryCommit = ledger.CommitMutation(
                ledger.CurrentEpoch,
                finalBlob,
                finalTokens,
                retryProof,
                _zkEngine,
                _timeProvider);

            retryCommit.IsSuccess.Should().BeTrue("Rebased mutation against Epoch 1 must succeed.");
            ledger.CurrentEpoch.Value.Should().Be(2);
            ledger.ActiveRoutingTokens.Should().BeEquivalentTo(finalTokens);
        }
    }

    private static byte[] ComputeChallenge(EncryptedEntriesBlob blob, IReadOnlySet<BlindedRoutingToken> tokens)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        sha.AppendData(blob.Span);

        Span<byte> guidBytes = stackalloc byte[16];
        foreach (var token in tokens.OrderBy(t => t.Value))
        {
            token.TryWriteBytes(guidBytes);
            sha.AppendData(guidBytes);
        }

        return sha.GetHashAndReset();
    }
}
