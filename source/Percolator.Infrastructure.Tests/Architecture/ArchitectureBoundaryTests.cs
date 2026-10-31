namespace Percolator.Infrastructure.Tests.Architecture;

using System;
using System.ComponentModel;
using System.Linq;
using System.Reflection;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;

[TestFixture]
public sealed class ArchitectureBoundaryTests
{
    [Test]
    public void AllRehydrateMethods_MustBeMarkedWithEditorBrowsableNever()
    {
        var targetTypes = new[]
        {
            typeof(IdentityProfile),
            typeof(PeerContact),
            typeof(DirectChannel),
            typeof(GroupChannel),
            typeof(RelayGroupLedger),
            typeof(RelayHostedPreKeyBundle),
            typeof(DirectRatchetSession),
            typeof(GroupSenderKeyRatchet),
            typeof(GroupReceiverSession),
            typeof(OutboxJob),
            typeof(TransferSession)
        };

        foreach (var type in targetTypes)
        {
            var rehydrateMethods = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.Name == "Rehydrate")
                .ToList();

            rehydrateMethods.Should().NotBeEmpty($"Type {type.Name} should have a static Rehydrate method.");

            foreach (var method in rehydrateMethods)
            {
                var attr = method.GetCustomAttribute<EditorBrowsableAttribute>();
                attr.Should().NotBeNull($"Method {type.Name}.Rehydrate must carry [EditorBrowsable(EditorBrowsableState.Never)]");
                attr!.State.Should().Be(EditorBrowsableState.Never);
            }
        }
    }

    [Test]
    public void CryptographicSessionAggregates_DoNotExposeRawMutableSecretProperties()
    {
        var cryptoAggregateTypes = new[]
        {
            typeof(DirectRatchetSession),
            typeof(GroupSenderKeyRatchet),
            typeof(GroupReceiverSession)
        };

        foreach (var type in cryptoAggregateTypes)
        {
            var sensitiveProperties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => typeof(ISensitiveSecret).IsAssignableFrom(p.PropertyType))
                .ToList();

            sensitiveProperties.Should().BeEmpty(
                $"Type {type.Name} must not expose public properties implementing ISensitiveSecret directly: " +
                string.Join(", ", sensitiveProperties.Select(p => p.Name)));
        }
    }

    [Test]
    public void IdentityProfile_Rehydrate_EnforcesStructuralInvariants()
    {
        var validId = PublicIdentityId.New();
        var now = DateTimeOffset.UtcNow;

        // Empty ID throws
        Action actEmptyId = () => IdentityProfile.Rehydrate(new PublicIdentityId(Guid.Empty), "Alice", IdentityRole.UserPersona, IdentityState.Active, now);
        actEmptyId.Should().Throw<ArgumentException>();

        // Empty display name throws
        Action actEmptyName = () => IdentityProfile.Rehydrate(validId, "   ", IdentityRole.UserPersona, IdentityState.Active, now);
        actEmptyName.Should().Throw<ArgumentException>();

        // Disabled state without DisabledAtUtc throws
        Action actDisabledNoTimestamp = () => IdentityProfile.Rehydrate(validId, "Alice", IdentityRole.UserPersona, IdentityState.Disabled, now, disabledAtUtc: null);
        actDisabledNoTimestamp.Should().Throw<ArgumentException>();

        // Active state with DisabledAtUtc throws
        Action actActiveWithTimestamp = () => IdentityProfile.Rehydrate(validId, "Alice", IdentityRole.UserPersona, IdentityState.Active, now, disabledAtUtc: now);
        actActiveWithTimestamp.Should().Throw<ArgumentException>();
    }

    [Test]
    public void PeerContact_Rehydrate_EnforcesStructuralInvariants()
    {
        var ownerId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var nickname = ContactNickname.Create("Bob", remoteId);
        var now = DateTimeOffset.UtcNow;
        var identityKey = IdentityKey.FromSpan(new byte[32]);

        // Self contact throws
        Action actSelf = () => PeerContact.Rehydrate(ownerId, ownerId, nickname, PeerTrustLevel.Verified, ContactState.Active, now, null, identityKey);
        actSelf.Should().Throw<ArgumentException>();

        // Empty owner ID throws
        Action actEmptyOwner = () => PeerContact.Rehydrate(new PublicIdentityId(Guid.Empty), remoteId, nickname, PeerTrustLevel.Verified, ContactState.Active, now, null, identityKey);
        actEmptyOwner.Should().Throw<ArgumentException>();

        // Exceeding max registered devices throws
        var excessiveDevices = Enumerable.Range(1, 35).Select(i => new DeviceId((uint)i));
        Action actExcessiveDevices = () => PeerContact.Rehydrate(ownerId, remoteId, nickname, PeerTrustLevel.Verified, ContactState.Active, now, null, identityKey, excessiveDevices);
        actExcessiveDevices.Should().Throw<ArgumentException>();
    }

    [Test]
    public void DirectChannel_Rehydrate_EnforcesStructuralInvariants()
    {
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var remoteId = PublicIdentityId.New();
        var now = DateTimeOffset.UtcNow;

        // Self channel throws
        Action actSelf = () => DirectChannel.Rehydrate(channelId, ownerId, ownerId, now, now);
        actSelf.Should().Throw<ArgumentException>();

        // Duplicate payload ID throws
        var payloadId = PayloadId.New();
        var payload1 = new ChannelPayload(payloadId, channelId, ownerId, DeviceId.Primary, new byte[] { 1, 2, 3 }, now);
        var payload2 = new ChannelPayload(payloadId, channelId, ownerId, DeviceId.Primary, new byte[] { 4, 5, 6 }, now);
        Action actDupPayload = () => DirectChannel.Rehydrate(channelId, ownerId, remoteId, now, now, new[] { payload1, payload2 });
        actDupPayload.Should().Throw<ArgumentException>();
    }

    [Test]
    public void GroupChannel_Rehydrate_EnforcesStructuralInvariants()
    {
        var channelId = ChannelId.New();
        var creatorId = PublicIdentityId.New();
        var now = DateTimeOffset.UtcNow;

        // Empty members throws
        Action actEmptyMembers = () => GroupChannel.Rehydrate(channelId, "Test Group", EpochNumber.Genesis, now, now, Enumerable.Empty<ChannelMember>());
        actEmptyMembers.Should().Throw<ArgumentException>();

        // Members without Admin throws
        var nonAdminMember = new ChannelMember(creatorId, ChannelRole.Member, now);
        Action actNoAdmin = () => GroupChannel.Rehydrate(channelId, "Test Group", EpochNumber.Genesis, now, now, new[] { nonAdminMember });
        actNoAdmin.Should().Throw<ArgumentException>();

        // Duplicate member throws
        var admin1 = new ChannelMember(creatorId, ChannelRole.Admin, now);
        var admin2 = new ChannelMember(creatorId, ChannelRole.Admin, now);
        Action actDupMember = () => GroupChannel.Rehydrate(channelId, "Test Group", EpochNumber.Genesis, now, now, new[] { admin1, admin2 });
        actDupMember.Should().Throw<ArgumentException>();
    }

    [Test]
    public void OutboxJob_Rehydrate_EnforcesStructuralInvariants()
    {
        var channelId = ChannelId.New();
        var ownerId = PublicIdentityId.New();
        var now = DateTimeOffset.UtcNow;
        var route = new DeliveryRoute(DeliveryRouteType.DirectP2P, new Uri("https://127.0.0.1:5000"), null);

        // Guid.Empty throws
        Action actEmptyGuid = () => OutboxJob.Rehydrate(Guid.Empty, channelId, ownerId, null, route, new byte[] { 1 }, OutboxStatus.Pending, 0, 5, now, null);
        actEmptyGuid.Should().Throw<ArgumentException>();

        // Non-positive max attempts throws
        Action actZeroAttempts = () => OutboxJob.Rehydrate(Guid.NewGuid(), channelId, ownerId, null, route, new byte[] { 1 }, OutboxStatus.Pending, 0, 0, now, null);
        actZeroAttempts.Should().Throw<ArgumentException>();

        // Negative retry count throws
        Action actNegRetry = () => OutboxJob.Rehydrate(Guid.NewGuid(), channelId, ownerId, null, route, new byte[] { 1 }, OutboxStatus.Pending, -1, 5, now, null);
        actNegRetry.Should().Throw<ArgumentException>();
    }
}
