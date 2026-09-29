using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Events;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.Tests.TestDoubles;

namespace Percolator.Domain.Tests.Delivery;

[TestFixture]
public class RelayGroupLedgerTests
{
    private FakeDateTimeProvider _timeProvider = null!;
    private FakeZkProofEngine _zkEngine = null!;
    private ChannelId _channelId;
    private PublicIdentityId _relayIdentityId;
    private EncryptedEntriesBlob _dummyBlob = null!;
    private ZkGroupPublicParams _dummyParams = null!;
    private ZkPresentationBytes _dummyProof = null!;

    [SetUp]
    public void SetUp()
    {
        _timeProvider = new FakeDateTimeProvider(new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero));
        _zkEngine = new FakeZkProofEngine();
        _channelId = ChannelId.New();
        _relayIdentityId = PublicIdentityId.New();

        _dummyBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 10, 20, 30 });
        _dummyParams = ZkGroupPublicParams.FromSpan(new byte[] { 1, 2, 3 });
        _dummyProof = ZkPresentationBytes.FromSpan(new byte[] { 99 });
    }

    [Test]
    public void CreateGenesis_WithEpochZeroAndNonEmptyRoster_Succeeds()
    {
        var token = BlindedRoutingToken.New();
        var result = RelayGroupLedger.CreateGenesis(
            _channelId,
            _relayIdentityId,
            _dummyBlob,
            new HashSet<BlindedRoutingToken> { token },
            _dummyParams,
            _timeProvider);

        result.IsSuccess.Should().BeTrue();
        var ledger = result.Value;
        ledger.CurrentEpoch.Value.Should().Be(0);
        ledger.ActiveRoutingTokens.Should().Contain(token);
    }

    [Test]
    public void CreateGenesis_WithEmptyRoster_ReturnsError()
    {
        var result = RelayGroupLedger.CreateGenesis(
            _channelId,
            _relayIdentityId,
            _dummyBlob,
            new HashSet<BlindedRoutingToken>(), // empty!
            _dummyParams,
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EMPTY_ROSTER");
    }

    [Test]
    public void CommitMutation_WithMatchingBaseEpochAndValidProof_AdvancesEpoch_AndEmitsEvent()
    {
        var token1 = BlindedRoutingToken.New();
        var ledger = RelayGroupLedger.CreateGenesis(
            _channelId,
            _relayIdentityId,
            _dummyBlob,
            new HashSet<BlindedRoutingToken> { token1 },
            _dummyParams,
            _timeProvider).Value;

        ledger.ClearDomainEvents();

        var token2 = BlindedRoutingToken.New();
        var newBlob = EncryptedEntriesBlob.FromSpan(new byte[] { 40, 50 });

        var result = ledger.CommitMutation(
            baseEpoch: EpochNumber.Genesis,
            newBlob: newBlob,
            newTokens: new HashSet<BlindedRoutingToken> { token1, token2 },
            proof: _dummyProof,
            proofEngine: _zkEngine,
            _timeProvider);

        result.IsSuccess.Should().BeTrue();
        ledger.CurrentEpoch.Value.Should().Be(1);
        ledger.ActiveRoutingTokens.Should().Contain(token2);
        ledger.DomainEvents.Should().ContainSingle(e => e is EpochCommittedEvent);
    }

    [Test]
    public void CommitMutation_WithMismatchedBaseEpoch_ReturnsEpochConflict()
    {
        var token1 = BlindedRoutingToken.New();
        var ledger = RelayGroupLedger.CreateGenesis(
            _channelId,
            _relayIdentityId,
            _dummyBlob,
            new HashSet<BlindedRoutingToken> { token1 },
            _dummyParams,
            _timeProvider).Value;

        var result = ledger.CommitMutation(
            baseEpoch: new EpochNumber(5),
            newBlob: _dummyBlob,
            newTokens: new HashSet<BlindedRoutingToken> { token1 },
            proof: _dummyProof,
            proofEngine: _zkEngine,
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("EPOCH_CONFLICT");
    }

    [Test]
    public void CommitMutation_WithInvalidZkProof_ReturnsInvalidProofError()
    {
        var token1 = BlindedRoutingToken.New();
        var ledger = RelayGroupLedger.CreateGenesis(
            _channelId,
            _relayIdentityId,
            _dummyBlob,
            new HashSet<BlindedRoutingToken> { token1 },
            _dummyParams,
            _timeProvider).Value;

        _zkEngine.AlwaysValid = false;

        var result = ledger.CommitMutation(
            baseEpoch: EpochNumber.Genesis,
            newBlob: _dummyBlob,
            newTokens: new HashSet<BlindedRoutingToken> { token1 },
            proof: _dummyProof,
            proofEngine: _zkEngine,
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("INVALID_ZK_PROOF");
    }

    [Test]
    public void VerifyDispatch_WithValidProofBoundToCiphertext_Succeeds()
    {
        var token = BlindedRoutingToken.New();
        var ledger = RelayGroupLedger.CreateGenesis(
            _channelId,
            _relayIdentityId,
            _dummyBlob,
            new HashSet<BlindedRoutingToken> { token },
            _dummyParams,
            _timeProvider).Value;

        byte[] ciphertext = new byte[] { 1, 2, 3, 4 };
        var result = ledger.VerifyDispatch(_dummyProof, ciphertext, _zkEngine);

        result.IsSuccess.Should().BeTrue();
    }

    [Test]
    public void CommitMutation_WhenTokensExceedMaxCapacity_ReturnsError()
    {
        var token1 = BlindedRoutingToken.New();
        var ledger = RelayGroupLedger.CreateGenesis(
            _channelId,
            _relayIdentityId,
            _dummyBlob,
            new HashSet<BlindedRoutingToken> { token1 },
            _dummyParams,
            _timeProvider).Value;

        var oversizedRoster = Enumerable.Range(0, RelayGroupLedger.MaxGroupMembers + 1)
            .Select(_ => BlindedRoutingToken.New())
            .ToHashSet();

        var result = ledger.CommitMutation(
            baseEpoch: EpochNumber.Genesis,
            newBlob: _dummyBlob,
            newTokens: oversizedRoster,
            proof: _dummyProof,
            proofEngine: _zkEngine,
            _timeProvider);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("MAX_GROUP_CAPACITY_EXCEEDED");
    }
}
