namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Application.Hosting;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Model;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Ports;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;
using NUnit.Framework;

public sealed class RelayPolicyEnforcementIntegrationTests
{
    [Test]
    public async Task HostManaged_ApprovalRequired_AutoApproveLocalhost_And_ModerationWorkflow()
    {
        await using var topology = new TestNetworkTopology();
        var hostManagedPolicy = RelayHostingPolicy.HostManaged;
        var relay = await topology.CreateRelayAsync(hostManagedPolicy).ConfigureAwait(false);

        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);

        var aliceTokens = await GetOrGenerateTokensAsync(alice, relay).ConfigureAwait(false);
        var bobRoutingToken = BlindedRoutingToken.New();
        var bobDeliveryToken = DeliveryToken.GenerateRandom();
        var bobDrainToken = DeliveryToken.GenerateRandom();

        // 1. AutoApproveLocalhost fast-path: Pre-associated Alice in LocalRelayAssociations auto-approves
        var localAssocRepo = relay.Services.GetRequiredService<ILocalRelayAssociationRepository>();
        await localAssocRepo.AuthorizeAsync(relay.RelayIdentityId.ToString(), aliceTokens.RoutingToken).ConfigureAwait(false);

        var channelFactory = alice.Services.GetRequiredService<IPeerGrpcChannelFactory>();
        var channel = channelFactory.GetOrCreateChannel(relay.Endpoint.ToUri());
        var client = new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);

        var aliceRegReq = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(aliceTokens.RoutingToken.ToByteArray()),
            AuthorizedDeliveryToken = ByteString.CopyFrom(aliceTokens.DeliveryToken.Span),
            DrainToken = ByteString.CopyFrom(aliceTokens.DrainToken.Span)
        };
        var aliceResp = await client.RegisterMailboxAsync(aliceRegReq).ResponseAsync.ConfigureAwait(false);
        aliceResp.Success.Should().BeTrue();
        aliceResp.RegistrationStatus.Should().Be(RegistrationStatusProto.RegistrationStatusApproved);

        // 2. Non-associated Bob is staged in PendingMailboxRegistrations
        var bobRegReq = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(bobRoutingToken.ToByteArray()),
            AuthorizedDeliveryToken = ByteString.CopyFrom(bobDeliveryToken.Span),
            DrainToken = ByteString.CopyFrom(bobDrainToken.Span)
        };
        var bobResp = await client.RegisterMailboxAsync(bobRegReq).ResponseAsync.ConfigureAwait(false);
        bobResp.Success.Should().BeTrue();
        bobResp.RegistrationStatus.Should().Be(RegistrationStatusProto.RegistrationStatusPendingApproval);

        var pendingRepo = relay.Services.GetRequiredService<IPendingMailboxRegistrationRepository>();
        var pending = await pendingRepo.GetByApplicantRoutingTokenAsync(bobRoutingToken).ConfigureAwait(false);
        pending.Should().NotBeNull();
        pending!.Status.Should().Be(RegistrationModerationStatus.PendingApproval);

        // 3. Operator approves Bob via IExchangeRegistrationModerationService
        var moderation = relay.Services.GetRequiredService<IExchangeRegistrationModerationService>();
        var approveResult = await moderation.ApproveAsync(relay.RelayIdentityId, bobRoutingToken).ConfigureAwait(false);
        approveResult.IsSuccess.Should().BeTrue();

        var bobApprovedResp = await client.RegisterMailboxAsync(bobRegReq).ResponseAsync.ConfigureAwait(false);
        bobApprovedResp.Success.Should().BeTrue();
        bobApprovedResp.RegistrationStatus.Should().Be(RegistrationStatusProto.RegistrationStatusApproved);

        // 4. Operator rejects Bob -> next attempt aborts connection (StatusCode.Unavailable)
        var rejectResult = await moderation.RejectAsync(relay.RelayIdentityId, bobRoutingToken).ConfigureAwait(false);
        rejectResult.IsSuccess.Should().BeTrue();

        var rejectAct = async () => await client.RegisterMailboxAsync(bobRegReq).ResponseAsync.ConfigureAwait(false);
        var rpcEx = await rejectAct.Should().ThrowAsync<RpcException>();
        rpcEx.Which.StatusCode.Should().Match(s => s == StatusCode.Unavailable || s == StatusCode.Internal);
    }

    [Test]
    public async Task ClosedRegistration_AbortsNewRegistrations_WhileActiveMailboxesDrain()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        await alice.LinkRelayAsync(relay).ConfigureAwait(false);

        // Policy dynamically switched to Closed
        relay.Policy = relay.Policy with
        {
            RegistrationStrategy = RelayRegistrationStrategy.Closed,
            AutoApproveLocalhost = false
        };

        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);
        var bobRoutingToken = BlindedRoutingToken.New();
        var bobDeliveryToken = DeliveryToken.GenerateRandom();
        var bobDrainToken = DeliveryToken.GenerateRandom();

        var channelFactory = bob.Services.GetRequiredService<IPeerGrpcChannelFactory>();
        var channel = channelFactory.GetOrCreateChannel(relay.Endpoint.ToUri());
        var client = new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);

        var bobRegReq = new RegisterMailboxRequest
        {
            RoutingToken = ByteString.CopyFrom(bobRoutingToken.ToByteArray()),
            AuthorizedDeliveryToken = ByteString.CopyFrom(bobDeliveryToken.Span),
            DrainToken = ByteString.CopyFrom(bobDrainToken.Span)
        };

        var bobAct = async () => await client.RegisterMailboxAsync(bobRegReq).ResponseAsync.ConfigureAwait(false);
        var ex = await bobAct.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Match(s => s == StatusCode.Unavailable || s == StatusCode.Internal);

        // Alice (existing active mailbox) continues to operate normally
        var aliceRoutingToken = await alice.GetRoutingTokenAsync(relay).ConfigureAwait(false);
        var queueDepth = await relay.GetQueueDepthAsync(aliceRoutingToken).ConfigureAwait(false);
        queueDepth.Should().Be(0);
    }

    [Test]
    public async Task PreKeyBundle_RateLimiting_EnforcesResourceExhausted()
    {
        await using var topology = new TestNetworkTopology();
        var rateLimiterOptions = new AuthenticatedMemberRateLimiterOptions
        {
            MaxRequestsPerMinute = 2,
            Window = TimeSpan.FromMinutes(1)
        };
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default, rateLimiterOptions).ConfigureAwait(false);

        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);

        var aliceRoutingToken = await alice.GetRoutingTokenAsync(relay).ConfigureAwait(false);
        var bobRoutingToken = await bob.GetRoutingTokenAsync(relay).ConfigureAwait(false);

        var relayDir = alice.Services.GetRequiredService<IRelayDirectory>();
        var aliceTokens = (await relayDir.GetMailboxTokensAsync(alice.IdentityId, relay.RelayIdentityId).ConfigureAwait(false)).Value!;

        var channelFactory = alice.Services.GetRequiredService<IPeerGrpcChannelFactory>();
        var channel = channelFactory.GetOrCreateChannel(relay.Endpoint.ToUri());
        var client = new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);

        var fetchReq = new FetchPreKeyBundleRequestProto
        {
            RoutingToken = ByteString.CopyFrom(aliceTokens.RoutingToken.ToByteArray()),
            DrainToken = ByteString.CopyFrom(aliceTokens.DrainToken.Span),
            TargetRoutingToken = ByteString.CopyFrom(bobRoutingToken.ToByteArray()),
            DeviceId = 1
        };

        // Call 1: Allowed
        var resp1 = await client.FetchPreKeyBundleAsync(fetchReq).ResponseAsync.ConfigureAwait(false);
        resp1.Should().NotBeNull();

        // Call 2: Allowed
        var resp2 = await client.FetchPreKeyBundleAsync(fetchReq).ResponseAsync.ConfigureAwait(false);
        resp2.Should().NotBeNull();

        // Call 3: Exceeds rate limit -> ResourceExhausted
        var act3 = async () => await client.FetchPreKeyBundleAsync(fetchReq).ResponseAsync.ConfigureAwait(false);
        var ex = await act3.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
    }

    [Test]
    public async Task DisabledPreKeysFeature_ReturnsPermissionDenied()
    {
        await using var topology = new TestNetworkTopology();
        var relay = await topology.CreateRelayAsync(RelayHostingPolicy.Default).ConfigureAwait(false);

        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await topology.CreatePersonaAsync("Bob").ConfigureAwait(false);

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);

        // Disable PreKeys feature on relay
        relay.Policy = relay.Policy with
        {
            EnabledFeatures = relay.Policy.EnabledFeatures & ~RelayFeatureFlags.PreKeys
        };

        var aliceRoutingToken = await alice.GetRoutingTokenAsync(relay).ConfigureAwait(false);
        var bobRoutingToken = await bob.GetRoutingTokenAsync(relay).ConfigureAwait(false);

        var relayDir = alice.Services.GetRequiredService<IRelayDirectory>();
        var aliceTokens = (await relayDir.GetMailboxTokensAsync(alice.IdentityId, relay.RelayIdentityId).ConfigureAwait(false)).Value!;

        var channelFactory = alice.Services.GetRequiredService<IPeerGrpcChannelFactory>();
        var channel = channelFactory.GetOrCreateChannel(relay.Endpoint.ToUri());
        var client = new AuthenticatedRelayService.AuthenticatedRelayServiceClient(channel);

        var fetchReq = new FetchPreKeyBundleRequestProto
        {
            RoutingToken = ByteString.CopyFrom(aliceTokens.RoutingToken.ToByteArray()),
            DrainToken = ByteString.CopyFrom(aliceTokens.DrainToken.Span),
            TargetRoutingToken = ByteString.CopyFrom(bobRoutingToken.ToByteArray()),
            DeviceId = 1
        };

        var act = async () => await client.FetchPreKeyBundleAsync(fetchReq).ResponseAsync.ConfigureAwait(false);
        var ex = await act.Should().ThrowAsync<RpcException>();
        ex.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);
    }

    [Test]
    public async Task MailboxByteQuota_And_MissingDeliveryToken_Enforcement()
    {
        await using var topology = new TestNetworkTopology();
        var policy = RelayHostingPolicy.Default with
        {
            MaxMailboxBytes = 500
        };
        var relay = await topology.CreateRelayAsync(policy).ConfigureAwait(false);

        var alice = await topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        await alice.LinkRelayAsync(relay).ConfigureAwait(false);

        var relayDir = alice.Services.GetRequiredService<IRelayDirectory>();
        var aliceTokens = (await relayDir.GetMailboxTokensAsync(alice.IdentityId, relay.RelayIdentityId).ConfigureAwait(false)).Value!;

        var channelFactory = alice.Services.GetRequiredService<IPeerGrpcChannelFactory>();
        var unauthChannel = channelFactory.GetOrCreateChannel(relay.Endpoint.ToUri());
        var unauthClient = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(unauthChannel);

        // 1. Missing DeliveryToken rejected with PermissionDenied
        var missingTokenReq = new EnqueueMailboxRequestProto
        {
            RecipientRoutingToken = ByteString.CopyFrom(aliceTokens.RoutingToken.ToByteArray()),
            DeliveryToken = ByteString.Empty,
            Envelope = new SealedEnvelopeProto
            {
                EncryptedPackage = ByteString.CopyFrom(new byte[100])
            }
        };

        var actMissing = async () => await unauthClient.EnqueueMailboxAsync(missingTokenReq).ResponseAsync.ConfigureAwait(false);
        var exMissing = await actMissing.Should().ThrowAsync<RpcException>();
        exMissing.Which.StatusCode.Should().Be(StatusCode.PermissionDenied);

        // 2. Envelope size exceeding MaxMailboxBytes rejected with ResourceExhausted
        var oversizedReq = new EnqueueMailboxRequestProto
        {
            RecipientRoutingToken = ByteString.CopyFrom(aliceTokens.RoutingToken.ToByteArray()),
            DeliveryToken = ByteString.CopyFrom(aliceTokens.DeliveryToken.Span),
            Envelope = new SealedEnvelopeProto
            {
                EncryptedPackage = ByteString.CopyFrom(new byte[1000])
            }
        };

        var actOversized = async () => await unauthClient.EnqueueMailboxAsync(oversizedReq).ResponseAsync.ConfigureAwait(false);
        var exOversized = await actOversized.Should().ThrowAsync<RpcException>();
        exOversized.Which.StatusCode.Should().Be(StatusCode.ResourceExhausted);
    }

    private static async Task<MailboxTokenTriad> GetOrGenerateTokensAsync(TestPersonaNode node, TestRelayServer relay)
    {
        var relayDir = node.Services.GetRequiredService<IRelayDirectory>();
        var reg = await relayDir.RegisterRemoteRelayAsync(relay.RelayIdentityId, relay.Endpoint).ConfigureAwait(false);
        if (reg.IsFailure && reg.Error.Code != "RELAY_ALREADY_REGISTERED")
        {
            throw new InvalidOperationException($"RegisterRemoteRelay failed: {reg.Error.Description}");
        }

        var link = await relayDir.LinkIdentityAsync(relay.RelayIdentityId, node.IdentityId).ConfigureAwait(false);
        if (link.IsFailure && link.Error.Code != "IDENTITY_ALREADY_LINKED")
        {
            throw new InvalidOperationException($"LinkIdentity failed: {link.Error.Description}");
        }

        var tokens = await relayDir.GetMailboxTokensAsync(node.IdentityId, relay.RelayIdentityId).ConfigureAwait(false);
        if (!tokens.IsSuccess)
        {
            throw new InvalidOperationException($"Failed to get mailbox tokens: {tokens.Error.Description}");
        }
        return tokens.Value!;
    }
}
