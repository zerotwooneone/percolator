using System.Security.Cryptography;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Delivery;
using Percolator.Application2.Delivery.Ports;
using Percolator.Application2IntegrationTests.Builders;
using Percolator.Application2IntegrationTests.Infrastructure;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Model;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.ValueObjects;
using Percolator.PluginSdk;
using DeliveryRoute = Percolator.PluginSdk.DeliveryRoute;

namespace Percolator.Application2IntegrationTests;

[TestFixture]
public sealed class MediaAttachmentScenarioTests
{
    [Test]
    public async Task Phase5_Alice_Uploads_And_Shares_Encrypted_Media_Attachment_Over_Relay()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Build cluster with Alice (15001), Charlie (15004), and Relay (15003)
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var relayPort = new EndpointPort(15003);
        var charliePort = new EndpointPort(15004);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddRelayNode("Relay", relayPort)
            .AddNode("Charlie", charliePort)
            .Build();

        var alice = cluster["Alice"];
        var charlie = cluster["Charlie"];
        var relay = cluster.GetRelay("Relay");

        // Establish Relayed 1:1 session between Alice and Charlie
        var channelId = await alice.EstablishRelayedSessionWithAsync(charlie, relay);

        // Generate 128 KB of realistic binary data
        var originalData = new byte[128 * 1024];
        RandomNumberGenerator.Fill(originalData);

        // ----------------------------------------------------------------------------------
        // Step 5.1: Alice uploads 128 KB binary payload via ChatMediaService
        // ----------------------------------------------------------------------------------
        using var uploadStream = new MemoryStream(originalData);
        var uploadResult = await alice.MediaService.UploadOutOfBandAttachmentAsync(
            uploadStream,
            originalData.Length,
            channelId,
            "beach_sunset.jpg",
            "image/jpeg");

        uploadResult.IsSuccess.Should().BeTrue(uploadResult.Error.Description);
        var uploadedAttachment = uploadResult.Value;

        // ----------------------------------------------------------------------------------
        // Step 5.2: Relay blob store stores encrypted chunks and returns BlobReference
        // ----------------------------------------------------------------------------------
        uploadedAttachment.OutOfBandReference.Should().NotBeNull();
        var blobRef = uploadedAttachment.OutOfBandReference!;

        blobRef.StorageMode.Should().Be(BlobStorageMode.ChannelRelay);
        blobRef.PlaintextSizeBytes.Should().Be(originalData.Length);
        blobRef.CiphertextSizeBytes.Should().BeGreaterThan(originalData.Length, "STREAM AEAD adds chunk tags and padding");
        blobRef.CiphertextSha256.Should().NotBeNullOrEmpty();
        blobRef.EncryptionKey.Should().HaveCount(32);
        blobRef.BaseNonce.Should().HaveCount(12);

        // Assert Relay blob store holds the encrypted payload
        relay.BlobStore.HasBlob(blobRef.Id).Should().BeTrue();
        var relayBlob = relay.BlobStore.GetBlob(blobRef.Id);
        relayBlob.Should().NotBeNull();
        relayBlob!.Length.Should().Be((int)blobRef.CiphertextSizeBytes);

        // Assert Alice's local chunk store cached the ciphertext for replay
        alice.BlobStorage.IsCachedLocally(blobRef.Id).Should().BeTrue();

        // ----------------------------------------------------------------------------------
        // Step 5.3: Alice sends chat message containing the attachment metadata
        // ----------------------------------------------------------------------------------
        var sendCmd = new SendMessageCommand(
            channelId,
            alice.IdentityId,
            charlie.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.RelayedOneToOne,
            "Here is the beach photo!",
            Attachments: [uploadedAttachment]);

        var sendResult = await alice.ChatService.SendMessageAsync(sendCmd);
        sendResult.IsSuccess.Should().BeTrue(sendResult.Error.Description);

        // Flush Alice's outbox worker to deliver envelope to Relay mailbox
        var processed = await alice.OutboxWorker.ProcessBatchAsync();
        processed.Should().Be(1);

        // ----------------------------------------------------------------------------------
        // Step 5.4: Charlie drains mailbox, receives chat message with attachment
        // ----------------------------------------------------------------------------------
        var drained = await charlie.DrainMailboxAsync(relay);
        drained.Should().Be(1);

        // Strict CQRS: Charlie queries chat messages
        ulong nowMs = (ulong)charlie.TimeProvider.UtcNow.ToUnixTimeMilliseconds();
        var charlieMessages = await charlie.ChatQueries.GetMessagesAsync(channelId, null, 50, nowMs);
        charlieMessages.Should().ContainSingle();
        var receivedMsg = charlieMessages[0];
        receivedMsg.AuthorId.Should().Be(alice.IdentityId);
        receivedMsg.Content.Should().Be("Here is the beach photo!");
        receivedMsg.Attachments.Should().ContainSingle();

        var receivedAttachment = receivedMsg.Attachments[0];
        receivedAttachment.BlobId.Should().Be(blobRef.Id);
        receivedAttachment.ByteSize.Should().Be(originalData.Length);
        receivedAttachment.OutOfBandReference.Should().NotBeNull();
        receivedAttachment.OutOfBandReference!.CiphertextSha256.Should().Equal(blobRef.CiphertextSha256);

        // Charlie does not have the ciphertext cached yet
        charlie.BlobStorage.IsCachedLocally(blobRef.Id).Should().BeFalse();

        // Charlie downloads the attachment via ChatMediaService
        var downloadResult = await charlie.MediaService.DownloadOutOfBandAttachmentAsync(receivedAttachment);
        downloadResult.IsSuccess.Should().BeTrue(downloadResult.Error.Description);

        using var downloadedStream = new MemoryStream();
        await downloadResult.Value.ContentStream.CopyToAsync(downloadedStream);
        var downloadedBytes = downloadedStream.ToArray();

        // ----------------------------------------------------------------------------------
        // Step 5.5: Byte-for-byte fidelity and Local Chunk Store Caching
        // ----------------------------------------------------------------------------------
        downloadedBytes.Should().Equal(originalData, "Downloaded plaintext must match original uploaded bytes byte-for-byte");
        charlie.BlobStorage.IsCachedLocally(blobRef.Id).Should().BeTrue("Downloaded blob should now be cached locally");

        // Verify local chunk cache resilience: delete blob from relay, Charlie should still download from local cache
        relay.BlobStore.DeleteBlob(blobRef.Id);
        relay.BlobStore.HasBlob(blobRef.Id).Should().BeFalse();

        var localDownloadResult = await charlie.MediaService.DownloadOutOfBandAttachmentAsync(receivedAttachment);
        localDownloadResult.IsSuccess.Should().BeTrue();
        using var localStream = new MemoryStream();
        await localDownloadResult.Value.ContentStream.CopyToAsync(localStream);
        localStream.ToArray().Should().Equal(originalData);

        // Verify local cache purge when message is tombstoned/deleted
        await charlie.MediaService.PurgeLocalAttachmentBlobsAsync([receivedAttachment]);
        charlie.BlobStorage.IsCachedLocally(blobRef.Id).Should().BeFalse("Purge should remove cached blob from local chunk store");
    }

    [Test]
    public async Task Phase5_Tampered_Ciphertext_Fails_AEAD_Verification()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Alice (15001), Charlie (15004), and Relay (15003)
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var relayPort = new EndpointPort(15003);
        var charliePort = new EndpointPort(15004);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddRelayNode("Relay", relayPort)
            .AddNode("Charlie", charliePort)
            .Build();

        var alice = cluster["Alice"];
        var charlie = cluster["Charlie"];
        var relay = cluster.GetRelay("Relay");

        var channelId = await alice.EstablishRelayedSessionWithAsync(charlie, relay);

        var originalData = new byte[64 * 1024];
        RandomNumberGenerator.Fill(originalData);

        using var uploadStream = new MemoryStream(originalData);
        var uploadResult = await alice.MediaService.UploadOutOfBandAttachmentAsync(
            uploadStream,
            originalData.Length,
            channelId,
            "document.pdf",
            "application/pdf");

        uploadResult.IsSuccess.Should().BeTrue();
        var attachment = uploadResult.Value;
        var blobRef = attachment.OutOfBandReference!;

        // Man-in-the-Middle tampers with the ciphertext stored on Relay
        var storedCiphertext = relay.BlobStore.GetBlob(blobRef.Id)!;
        var tamperedCiphertext = (byte[])storedCiphertext.Clone();
        tamperedCiphertext[16] ^= 0xAA; // Corrupt ciphertext byte
        relay.BlobStore.SaveBlob(blobRef.Id, tamperedCiphertext);

        // Charlie attempts to download without local cache
        charlie.BlobStorage.IsCachedLocally(blobRef.Id).Should().BeFalse();
        var downloadResult = await charlie.MediaService.DownloadOutOfBandAttachmentAsync(attachment);

        // AEAD verification must fail
        downloadResult.IsSuccess.Should().BeFalse();
        downloadResult.Error.Code.Should().Be("AEAD_VERIFICATION_FAILED");
    }

    [Test]
    public async Task Phase5_DirectP2P_Blob_Transfer_Between_Alice_And_Bob()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Alice (15001) and Bob (15002) in a direct LAN connection (no relay for channel)
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var bobPort = new EndpointPort(15002);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddNode("Bob", bobPort)
            .Build();

        var alice = cluster["Alice"];
        var bob = cluster["Bob"];

        // Establish Direct LAN session
        var channelId = await alice.EstablishDirectSessionWithAsync(bob);
        cluster.Switch.HasActiveStream(alice.Port, bob.Port).Should().BeTrue();

        var originalData = new byte[64 * 1024];
        RandomNumberGenerator.Fill(originalData);

        // Alice uploads attachment for direct channel (Topology A: Direct P2P)
        using var uploadStream = new MemoryStream(originalData);
        var uploadResult = await alice.MediaService.UploadOutOfBandAttachmentAsync(
            uploadStream,
            originalData.Length,
            channelId,
            "archive.zip",
            "application/zip");

        uploadResult.IsSuccess.Should().BeTrue();
        var attachment = uploadResult.Value;
        attachment.OutOfBandReference.Should().NotBeNull();
        attachment.OutOfBandReference!.StorageMode.Should().Be(BlobStorageMode.DirectP2P);

        // Alice stages blob in her local chunk store
        alice.BlobStorage.IsCachedLocally(attachment.BlobId).Should().BeTrue();

        // Alice sends chat message to Bob directly
        var sendCmd = new SendMessageCommand(
            channelId,
            alice.IdentityId,
            bob.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.DirectP2P,
            "Sending you a file directly!",
            Attachments: [attachment]);

        var sendResult = await alice.ChatService.SendMessageAsync(sendCmd);
        sendResult.IsSuccess.Should().BeTrue();

        // Bob queries chat messages (Strict CQRS)
        ulong nowMs = (ulong)bob.TimeProvider.UtcNow.ToUnixTimeMilliseconds();
        var bobMessages = await bob.ChatQueries.GetMessagesAsync(channelId, null, 50, nowMs);
        bobMessages.Should().ContainSingle();
        var receivedMsg = bobMessages[0];
        receivedMsg.Attachments.Should().ContainSingle();

        var receivedAttachment = receivedMsg.Attachments[0];

        // Bob downloads directly from Alice's local chunk store via PeerStream
        var downloadResult = await bob.MediaService.DownloadOutOfBandAttachmentAsync(
            receivedAttachment,
            remotePeerId: alice.IdentityId);

        downloadResult.IsSuccess.Should().BeTrue(downloadResult.Error.Description);
        using var downloadedStream = new MemoryStream();
        await downloadResult.Value.ContentStream.CopyToAsync(downloadedStream);
        downloadedStream.ToArray().Should().Equal(originalData);

        // Bob now has it cached locally
        bob.BlobStorage.IsCachedLocally(attachment.BlobId).Should().BeTrue();
    }

    [Test]
    public async Task Phase5_Group_Chat_Media_Attachment_Broadcast_To_Bob_And_Charlie()
    {
        // ----------------------------------------------------------------------------------
        // Setup: Alice (15001), Bob (15002), Charlie (15004), and Relay (15003)
        // ----------------------------------------------------------------------------------
        var alicePort = new EndpointPort(15001);
        var bobPort = new EndpointPort(15002);
        var relayPort = new EndpointPort(15003);
        var charliePort = new EndpointPort(15004);

        var cluster = new TestClusterBuilder()
            .AddNode("Alice", alicePort)
            .AddNode("Bob", bobPort)
            .AddRelayNode("Relay", relayPort)
            .AddNode("Charlie", charliePort)
            .Build();

        var alice = cluster["Alice"];
        var bob = cluster["Bob"];
        var charlie = cluster["Charlie"];
        var relay = cluster.GetRelay("Relay");

        // Register mailboxes on Relay
        alice.RegisterMailbox(relay);
        bob.RegisterMailbox(relay);
        charlie.RegisterMailbox(relay);

        var relayEndpoint = RelayEndpoint.FromHostAndPort("localhost", relay.Port.Value);
        bob.AddPeerMailboxRoute(charlie.IdentityId, relayEndpoint, charlie.MailboxRoutingToken, charlie.MailboxDeliveryToken);
        charlie.AddPeerMailboxRoute(bob.IdentityId, relayEndpoint, bob.MailboxRoutingToken, bob.MailboxDeliveryToken);

        var aliceBobChannelId = await alice.EstablishDirectSessionWithAsync(bob);
        var aliceCharlieChannelId = await alice.EstablishRelayedSessionWithAsync(charlie, relay);

        // 1. Group Channel Setup
        var groupChannelId = ChannelId.New();
        const string groupName = "Media Group";

        var genesisGroupResult = GroupChannel.CreateGenesis(groupChannelId, alice.IdentityId, groupName, alice.TimeProvider);
        genesisGroupResult.IsSuccess.Should().BeTrue();
        var groupChannel = genesisGroupResult.Value!;
        groupChannel.AddMember(alice.IdentityId, bob.IdentityId, ChannelRole.Member, alice.TimeProvider);
        groupChannel.AddMember(alice.IdentityId, charlie.IdentityId, ChannelRole.Member, alice.TimeProvider);

        await alice.SaveGroupChannelInternalAsync(groupChannel);

        var masterKeyBytes = new byte[32];
        RandomNumberGenerator.Fill(masterKeyBytes);
        using var groupMasterKey = GroupMasterKey.FromSpan(masterKeyBytes);

        var authMacBytes = new byte[32];
        RandomNumberGenerator.Fill(authMacBytes);
        var authMac = AuthCredentialMacBytes.FromSpan(authMacBytes);

        cluster.Switch.RegisterGroupCredentials(groupChannelId, groupMasterKey, authMac);
        cluster.Switch.RegisterRelayForChannel(groupChannelId, relay);

        var activeTokens = new HashSet<BlindedRoutingToken>
        {
            alice.MailboxRoutingToken,
            bob.MailboxRoutingToken,
            charlie.MailboxRoutingToken
        };
        var publicParams = ZkGroupPublicParams.FromBytesOwned(new byte[64]);
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[128]);

        var ledgerResult = RelayGroupLedger.CreateGenesis(
            groupChannelId,
            relay.RelayIdentityId,
            genesisBlob,
            activeTokens,
            publicParams,
            alice.TimeProvider);

        ledgerResult.IsSuccess.Should().BeTrue();
        var ledger = ledgerResult.Value!;
        relay.RegisterGroupLedger(ledger);

        // Alice invites Bob and Charlie
        ulong expiryMs = (ulong)alice.TimeProvider.UtcNow.AddDays(7).ToUnixTimeMilliseconds();

        var bobInvitationId = InvitationId.New();
        var bobInviteDto = new GroupInvitationDto(
            bobInvitationId,
            groupChannelId,
            groupName,
            alice.IdentityId,
            $"localhost:{relay.Port.Value}",
            expiryMs);

        var bobInviteResult = await alice.GroupInvitationService.SendInvitationAsync(
            bobInviteDto,
            aliceBobChannelId,
            bob.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.DirectP2P);
        bobInviteResult.IsSuccess.Should().BeTrue();

        var charlieInvitationId = InvitationId.New();
        var charlieInviteDto = new GroupInvitationDto(
            charlieInvitationId,
            groupChannelId,
            groupName,
            alice.IdentityId,
            $"localhost:{relay.Port.Value}",
            expiryMs);

        var charlieInviteResult = await alice.GroupInvitationService.SendInvitationAsync(
            charlieInviteDto,
            aliceCharlieChannelId,
            charlie.IdentityId,
            DeviceId.Primary,
            DeliveryRoute.RelayedOneToOne);
        charlieInviteResult.IsSuccess.Should().BeTrue();

        await alice.OutboxWorker.ProcessBatchAsync();
        await charlie.DrainMailboxAsync(relay);

        // Bob and Charlie accept invitations
        var bobAcceptResult = await bob.GroupInvitationService.AcceptInvitationAsync(
            bobInvitationId,
            bob.IdentityId,
            DeviceId.Primary,
            aliceBobChannelId,
            DeliveryRoute.DirectP2P);
        bobAcceptResult.IsSuccess.Should().BeTrue();

        var charlieAcceptResult = await charlie.GroupInvitationService.AcceptInvitationAsync(
            charlieInvitationId,
            charlie.IdentityId,
            DeviceId.Primary,
            aliceCharlieChannelId,
            DeliveryRoute.RelayedOneToOne);
        charlieAcceptResult.IsSuccess.Should().BeTrue();

        await charlie.OutboxWorker.ProcessBatchAsync();
        await alice.DrainMailboxAsync(relay);

        // Alice distributes sender key to Bob and Charlie
        var distResult = await alice.GroupKeyDistributionService.DistributeSenderKeyAsync(
            groupChannelId,
            alice.IdentityId,
            DeviceId.Primary,
            [bob.IdentityId, charlie.IdentityId]);
        distResult.IsSuccess.Should().BeTrue();

        await alice.OutboxWorker.ProcessBatchAsync();
        await charlie.DrainMailboxAsync(relay);

        // 2. Alice uploads 128 KB media attachment
        var originalData = new byte[128 * 1024];
        RandomNumberGenerator.Fill(originalData);

        using var uploadStream = new MemoryStream(originalData);
        var uploadResult = await alice.MediaService.UploadOutOfBandAttachmentAsync(
            uploadStream,
            originalData.Length,
            groupChannelId,
            "project_spec.pdf",
            "application/pdf");

        uploadResult.IsSuccess.Should().BeTrue();
        var attachment = uploadResult.Value;

        // 3. Alice broadcasts group message with attachment
        var broadcastCmd = new SendMessageCommand(
            groupChannelId,
            alice.IdentityId,
            null,
            DeviceId.Primary,
            DeliveryRoute.RelayedGroup,
            "Team, please review the attached spec.",
            Attachments: [attachment]);

        var broadcastResult = await alice.ChatService.SendMessageAsync(broadcastCmd);
        broadcastResult.IsSuccess.Should().BeTrue();
        await alice.OutboxWorker.ProcessBatchAsync();

        // 4. Bob and Charlie drain relay mailbox
        var bobDrained = await bob.DrainMailboxAsync(relay);
        bobDrained.Should().Be(1);

        var charlieDrained = await charlie.DrainMailboxAsync(relay);
        charlieDrained.Should().Be(1);

        // 5. Both Bob and Charlie query message and download attachment from Relay
        ulong checkTimeMs = (ulong)alice.TimeProvider.UtcNow.ToUnixTimeMilliseconds();

        var bobMessages = await bob.ChatQueries.GetMessagesAsync(groupChannelId, null, 50, checkTimeMs);
        bobMessages.Should().ContainSingle();
        var bobMsg = bobMessages[0];
        bobMsg.Attachments.Should().ContainSingle();

        var bobDownload = await bob.MediaService.DownloadOutOfBandAttachmentAsync(bobMsg.Attachments[0]);
        bobDownload.IsSuccess.Should().BeTrue(bobDownload.Error.Description);
        using var bobMs = new MemoryStream();
        await bobDownload.Value.ContentStream.CopyToAsync(bobMs);
        bobMs.ToArray().Should().Equal(originalData);

        var charlieMessages = await charlie.ChatQueries.GetMessagesAsync(groupChannelId, null, 50, checkTimeMs);
        charlieMessages.Should().ContainSingle();
        var charlieMsg = charlieMessages[0];
        charlieMsg.Attachments.Should().ContainSingle();

        var charlieDownload = await charlie.MediaService.DownloadOutOfBandAttachmentAsync(charlieMsg.Attachments[0]);
        charlieDownload.IsSuccess.Should().BeTrue(charlieDownload.Error.Description);
        using var charlieMs = new MemoryStream();
        await charlieDownload.Value.ContentStream.CopyToAsync(charlieMs);
        charlieMs.ToArray().Should().Equal(originalData);
    }
}
