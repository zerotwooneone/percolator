using System.Security.Cryptography;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Domain.IntegrationTests.TestDoubles;

namespace Percolator.Domain.IntegrationTests.Builders;

public sealed class ScenarioGroupContext : IDisposable
{
    public GroupChannel Channel { get; }
    public RelayGroupLedger Ledger { get; }
    public GroupCredentials Credentials { get; }
    public ScenarioParticipant Admin { get; }
    public IReadOnlyList<ScenarioParticipant> Members { get; }

    private readonly Dictionary<PublicIdentityId, GroupSenderKeyRatchet> _senders = new();
    private readonly Dictionary<(PublicIdentityId Receiver, PublicIdentityId Author), GroupReceiverSession> _receivers = new();

    public ScenarioGroupContext(
        GroupChannel channel,
        RelayGroupLedger ledger,
        GroupCredentials credentials,
        ScenarioParticipant admin,
        IReadOnlyList<ScenarioParticipant> members)
    {
        Channel = channel;
        Ledger = ledger;
        Credentials = credentials;
        Admin = admin;
        Members = members;
    }

    public void RegisterSender(ScenarioParticipant participant, GroupSenderKeyRatchet ratchet)
    {
        _senders[participant.IdentityId] = ratchet;
    }

    public void RegisterReceiver(ScenarioParticipant receiver, ScenarioParticipant author, GroupReceiverSession session)
    {
        _receivers[(receiver.IdentityId, author.IdentityId)] = session;
    }

    public GroupSenderKeyRatchet GetSender(ScenarioParticipant participant)
        => _senders[participant.IdentityId];

    public GroupReceiverSession GetReceiver(ScenarioParticipant receiver, ScenarioParticipant author)
        => _receivers[(receiver.IdentityId, author.IdentityId)];

    /// <summary>
    /// Rotates the sender key for the specified author to a new KeyId and provisions updated receivers for active members.
    /// Used when members are revoked/removed to maintain backward secrecy.
    /// </summary>
    public (GroupSenderKeyRatchet NewSender, List<GroupReceiverSession> NewReceivers) RotateSenderKey(
        ScenarioParticipant author,
        uint newKeyId,
        IEnumerable<ScenarioParticipant> activeMembers,
        GroupMasterKey? freshMasterKey = null)
    {
        var masterKey = freshMasterKey ?? GroupMasterKey.FromSpan(RandomNumberGenerator.GetBytes(32));
        var newInitialChainKey = ChainKey.FromSpan(SHA256.HashData(masterKey.Span));

        var senderRatchet = new GroupSenderKeyRatchet(
            Channel.Id,
            author.IdentityId,
            author.DeviceId,
            newInitialChainKey,
            initialIteration: 0,
            keyId: newKeyId,
            signingPrivateKey: author.IdentityPrivateKey,
            authorSigningPublicKey: author.IdentityPublicKey);

        RegisterSender(author, senderRatchet);

        var receivers = new List<GroupReceiverSession>();
        foreach (var peer in activeMembers.Where(m => m.IdentityId != author.IdentityId))
        {
            var receiver = new GroupReceiverSession(
                Channel.Id,
                author.IdentityId,
                author.DeviceId,
                newInitialChainKey,
                initialIteration: 0,
                keyId: newKeyId,
                authorSigningKey: author.IdentityPublicKey);

            RegisterReceiver(peer, author, receiver);
            receivers.Add(receiver);
        }

        return (senderRatchet, receivers);
    }

    public void Dispose()
    {
        Credentials.Dispose();
        foreach (var sender in _senders.Values)
        {
            sender.Dispose();
        }
        foreach (var receiver in _receivers.Values)
        {
            receiver.Dispose();
        }
    }
}

public sealed class ScenarioGroupBuilder
{
    private readonly string _name;
    private readonly ScenarioParticipant _admin;
    private readonly List<ScenarioParticipant> _members = new();
    private readonly ScenarioCryptoEngine _cryptoEngine;
    private readonly IZkProofEngine _zkEngine;
    private readonly IDateTimeProvider _timeProvider;

    public ScenarioGroupBuilder(
        string name,
        ScenarioParticipant admin,
        ScenarioCryptoEngine cryptoEngine,
        IZkProofEngine zkEngine,
        IDateTimeProvider timeProvider)
    {
        _name = name;
        _admin = admin;
        _cryptoEngine = cryptoEngine;
        _zkEngine = zkEngine;
        _timeProvider = timeProvider;
        _members.Add(admin);
    }

    public static ScenarioGroupBuilder Create(
        string name,
        ScenarioParticipant admin,
        ScenarioCryptoEngine cryptoEngine,
        IZkProofEngine zkEngine,
        IDateTimeProvider timeProvider)
        => new(name, admin, cryptoEngine, zkEngine, timeProvider);

    public ScenarioGroupBuilder WithMember(ScenarioParticipant member)
    {
        if (!_members.Contains(member))
        {
            _members.Add(member);
        }
        return this;
    }

    public async Task<ScenarioGroupContext> BuildAsync(
        IGroupCredentialsRepository repo,
        PublicIdentityId? relayId = null)
    {
        var channelId = ChannelId.New();
        var relay = relayId ?? PublicIdentityId.New();

        // 1. GroupChannel
        var channelResult = GroupChannel.CreateGenesis(channelId, _admin.IdentityId, _name, _timeProvider);
        var channel = channelResult.Value!;

        foreach (var member in _members.Where(m => m != _admin))
        {
            channel.AddMember(_admin.IdentityId, member.IdentityId, ChannelRole.Member, _timeProvider);
        }

        // 2. GroupCredentials
        var masterBytes = new byte[32];
        RandomNumberGenerator.Fill(masterBytes);
        var masterKey = GroupMasterKey.FromSpan(masterBytes);

        var authMacBytes = new byte[32];
        RandomNumberGenerator.Fill(authMacBytes);
        var authMac = AuthCredentialMacBytes.FromSpan(authMacBytes);

        var credentials = GroupCredentials.CreateGenesis(channelId, masterKey, authMac).Value!;
        await repo.SaveAsync(credentials);

        // 3. RelayGroupLedger
        var tokens = _members.Select(_ => BlindedRoutingToken.New()).ToHashSet();
        var publicParams = ZkGroupPublicParams.FromBytesOwned(new byte[64]);
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[128]);

        var ledger = RelayGroupLedger.CreateGenesis(
            channelId,
            relay,
            genesisBlob,
            tokens,
            publicParams,
            _timeProvider).Value!;

        var context = new ScenarioGroupContext(channel, ledger, credentials, _admin, _members);

        // 4. Setup Sender ratchets & Receiver sessions for all members
        foreach (var author in _members)
        {
            var authorInitialChain = ChainKey.FromSpan(SHA256.HashData(credentials.MasterKey.Span));
            var senderRatchet = new GroupSenderKeyRatchet(
                channelId,
                author.IdentityId,
                author.DeviceId,
                authorInitialChain,
                initialIteration: 0,
                keyId: 1,
                signingPrivateKey: author.IdentityPrivateKey,
                authorSigningPublicKey: author.IdentityPublicKey);

            context.RegisterSender(author, senderRatchet);

            foreach (var peer in _members.Where(m => m != author))
            {
                var receiverSession = new GroupReceiverSession(
                    channelId,
                    author.IdentityId,
                    author.DeviceId,
                    authorInitialChain,
                    initialIteration: 0,
                    keyId: 1,
                    authorSigningKey: author.IdentityPublicKey);

                context.RegisterReceiver(peer, author, receiverSession);
            }
        }

        return context;
    }
}
