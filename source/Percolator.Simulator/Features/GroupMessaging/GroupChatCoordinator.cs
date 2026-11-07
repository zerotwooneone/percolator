using R3;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Percolator.Application.Ports;
using Percolator.Application2.Ports;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Domain.Channels.Model;
using Percolator.Domain.Channels.Ports;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.Hosting;
using Percolator.Domain.Delivery.Ports;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Domain.Security.Model;
using Percolator.Domain.Security.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Security;
using Percolator.Infrastructure.Transport;
using Percolator.PluginSdk;
using Percolator.Simulator.Features.DirectChat;
using Percolator.Simulator.Features.Logging;
using Percolator.Simulator.Features.RelayedChat;
using Percolator.Simulator.Features.RichChatAndProfiles;
using Percolator.Simulator.Features.Shell;
using Percolator.Simulator.Features.Topology;

namespace Percolator.Simulator.Features.GroupMessaging;

internal sealed class GroupChatCoordinator : IDisposable
{
    private readonly ISimulatedWorldStateService _worldState;
    private readonly ISimulatorStateBus _stateBus;
    private readonly IStatusToastService _toastService;
    private readonly ISimulatorLogSink _logSink;
    private readonly IVirtualClockStateService _virtualClock;
    private readonly ConcurrentDictionary<(SimulatedNodeId, ChannelId), CancellationTokenSource> _streamSubscriptions = new();
    private readonly ConcurrentDictionary<ChannelId, Uri> _groupRelays = new();
    private bool _disposed;

    public GroupChatCoordinator(
        ISimulatedWorldStateService worldState,
        ISimulatorStateBus stateBus,
        IStatusToastService toastService,
        ISimulatorLogSink logSink,
        IVirtualClockStateService virtualClock)
    {
        _worldState = worldState ?? throw new ArgumentNullException(nameof(worldState));
        _stateBus = stateBus ?? throw new ArgumentNullException(nameof(stateBus));
        _toastService = toastService ?? throw new ArgumentNullException(nameof(toastService));
        _logSink = logSink ?? throw new ArgumentNullException(nameof(logSink));
        _virtualClock = virtualClock ?? throw new ArgumentNullException(nameof(virtualClock));
    }

    public async Task<ChannelId> CreateGroupOnRelayAsync(
        ISimulatedPersonaHost admin,
        ISimulatedRelayHost relay,
        string groupName,
        IReadOnlyList<ISimulatedPersonaHost> members,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(relay);
        if (string.IsNullOrWhiteSpace(groupName)) throw new ArgumentException("Group name cannot be empty.", nameof(groupName));
        ArgumentNullException.ThrowIfNull(members);

        var channelId = ChannelId.New();
        _groupRelays[channelId] = relay.BoundEndpoint;

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            admin.DisplayName,
            admin.Id,
            "GroupProvisioning",
            $"Creating group '{groupName}' (ID: {channelId}) on relay '{relay.DisplayName}' with {members.Count} initial members."));

        // 1. Admin creates genesis GroupChannel aggregate
        var genesisResult = GroupChannel.CreateGenesis(channelId, admin.PublicIdentityId, groupName, _virtualClock);
        if (genesisResult.IsFailure)
        {
            throw new InvalidOperationException($"Failed to create genesis group: {genesisResult.Error!.Description}");
        }

        var groupChannel = genesisResult.Value!;
        foreach (var member in members)
        {
            if (member.PublicIdentityId != admin.PublicIdentityId)
            {
                groupChannel.AddMember(admin.PublicIdentityId, member.PublicIdentityId, ChannelRole.Member, _virtualClock);
            }
        }

        using (var adminScope = admin.Services.CreateScope())
        {
            var channelRepo = adminScope.ServiceProvider.GetRequiredService<IChannelRepository>();
            await channelRepo.SaveGroupAsync(groupChannel, ct);
        }

        // 2. Commit RelayGroupLedger on Relay
        var activeTokens = new HashSet<BlindedRoutingToken> { admin.MailboxRoutingToken };
        foreach (var member in members)
        {
            activeTokens.Add(member.MailboxRoutingToken);
        }

        var publicParams = ZkGroupPublicParams.FromBytesOwned(new byte[64]);
        var genesisBlob = EncryptedEntriesBlob.FromSpan(new byte[128]);
        var ledgerResult = RelayGroupLedger.CreateGenesis(
            channelId,
            relay.RelayIdentityId,
            genesisBlob,
            activeTokens,
            publicParams,
            _virtualClock);

        if (ledgerResult.IsFailure)
        {
            throw new InvalidOperationException($"Failed to create relay group ledger: {ledgerResult.Error!.Description}");
        }

        await relay.ProvisionGroupLedgerAsync(ledgerResult.Value!, ct);

        // 3. Admin subscribes to group stream on Relay
        SubscribePersonaToGroup(admin, channelId, relay.BoundEndpoint);

        // 4. Pairwise SenderKey Distribution from Admin to Members
        using (var adminScope = admin.Services.CreateScope())
        {
            var keyDist = adminScope.ServiceProvider.GetRequiredService<IGroupKeyDistributionService>();
            var recipientIds = members.Where(m => m.PublicIdentityId != admin.PublicIdentityId).Select(m => m.PublicIdentityId);
            var distResult = await keyDist.DistributeSenderKeyAsync(channelId, admin.PublicIdentityId, DeviceId.Primary, recipientIds, ct);
            if (distResult.IsFailure)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Warning,
                    admin.DisplayName,
                    admin.Id,
                    "GroupProvisioning",
                    $"SenderKey distribution warning: {distResult.Error!.Description}"));
            }

            // 5. Send pairwise GroupInvitations to all members
            var inviteService = adminScope.ServiceProvider.GetRequiredService<GroupInvitationService>();
            ulong expiryMs = (ulong)_virtualClock.UtcNow.AddDays(7).ToUnixTimeMilliseconds();

            foreach (var member in members)
            {
                if (member.PublicIdentityId == admin.PublicIdentityId) continue;

                var directChannelId = ChannelIdUtility.DeterministicFor(admin.PublicIdentityId, member.PublicIdentityId);
                var route = member.HomeRelay != null ? DeliveryRoute.RelayedOneToOne : DeliveryRoute.DirectP2P;

                var allMemberIds = members.Select(m => m.PublicIdentityId).Append(admin.PublicIdentityId).Distinct().ToList();
                var inviteDto = new GroupInvitationDto(
                    InvitationId.New(),
                    channelId,
                    groupName,
                    admin.PublicIdentityId,
                    relay.BoundEndpoint.ToString(),
                    expiryMs,
                    allMemberIds);

                var inviteResult = await inviteService.SendInvitationAsync(
                    inviteDto,
                    directChannelId,
                    member.PublicIdentityId,
                    DeviceId.Primary,
                    route,
                    ct);

                if (inviteResult.IsSuccess)
                {
                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Debug,
                        admin.DisplayName,
                        admin.Id,
                        "GroupProvisioning",
                        $"Group invitation sent to {member.DisplayName}."));

                    // Check member autopilot for instant acceptance
                    if (member.AutoAcceptGroupInvites)
                    {
                        var capturedInvite = inviteDto;
                        var capturedMember = member;
                        UiTaskRunner.FireAndForget(
                            () => AcceptGroupInviteAsync(capturedMember, capturedInvite.InvitationId),
                            "AutoAcceptGroupInvite",
                            _logSink);
                    }
                }
            }

            await admin.FlushOutboxAsync(ct);
        }

        foreach (var member in members)
        {
            if (member.HomeRelay != null)
            {
                await member.DrainMailboxAsync(ct);
            }
        }

        // 6. Register group summary in world state
        var summary = new SimulatedNodeSummary(
            new SimulatedNodeId(channelId.Value),
            groupName,
            SimulatedNodeType.Group,
            $"Relay: {relay.DisplayName} | Epoch 1",
            IsPinned: true);
        _worldState.RegisterGroup(summary);

        _stateBus.Publish(new SimulatorStateSignal(admin.Id, SignalCategory.GroupStateChanged, TargetChannelId: channelId));
        _toastService.ShowToast("Group Created", $"Group '{groupName}' created on {relay.DisplayName}.", ToastSeverity.Success);
        return channelId;
    }

    public async Task<bool> AcceptGroupInviteAsync(
        ISimulatedPersonaHost persona,
        InvitationId invitationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);

        using var scope = persona.Services.CreateScope();
        var inviteRepo = scope.ServiceProvider.GetRequiredService<IGroupInvitationRepository>();
        var invitation = await inviteRepo.GetByIdAsync(invitationId, ct);
        if (invitation == null)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                persona.DisplayName,
                persona.Id,
                "GroupInvitation",
                $"Invitation {invitationId} not found."));
            return false;
        }

        var directChannelId = ChannelIdUtility.DeterministicFor(persona.PublicIdentityId, invitation.InviterId);
        var route = persona.HomeRelay != null ? DeliveryRoute.RelayedOneToOne : DeliveryRoute.DirectP2P;

        var inviteService = scope.ServiceProvider.GetRequiredService<GroupInvitationService>();
        var acceptResult = await inviteService.AcceptInvitationAsync(
            invitationId,
            persona.PublicIdentityId,
            DeviceId.Primary,
            directChannelId,
            route,
            ct);

        if (acceptResult.IsFailure)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                persona.DisplayName,
                persona.Id,
                "GroupInvitation",
                $"Failed to accept group invite: {acceptResult.Error!.Description}"));
            return false;
        }

        // Subscribe invitee to group stream on relay
        if (Uri.TryCreate(invitation.RelayEndpoint, UriKind.Absolute, out var relayUri))
        {
            _groupRelays[invitation.TargetGroupChannelId] = relayUri;
            SubscribePersonaToGroup(persona, invitation.TargetGroupChannelId, relayUri);
        }

        // Distribute invitee's sender key to other group members
        var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
        var groupChannel = await channelRepo.GetGroupByIdAsync(invitation.TargetGroupChannelId, ct);
        if (groupChannel != null)
        {
            var keyDist = scope.ServiceProvider.GetRequiredService<IGroupKeyDistributionService>();
            var otherMemberIds = groupChannel.Members
                .Where(m => m.Id != persona.PublicIdentityId)
                .Select(m => m.Id)
                .ToList();

            await keyDist.DistributeSenderKeyAsync(
                groupChannel.Id,
                persona.PublicIdentityId,
                DeviceId.Primary,
                otherMemberIds,
                ct);

            await persona.FlushOutboxAsync(ct);

            foreach (var memberId in otherMemberIds)
            {
                var otherHost = _worldState.FindPersonaHostByIdentity(memberId);
                if (otherHost?.HomeRelay != null)
                {
                    await otherHost.DrainMailboxAsync(ct);
                }
            }
        }

        if (persona.HomeRelay != null)
        {
            await persona.DrainMailboxAsync(ct);
        }

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            persona.DisplayName,
            persona.Id,
            "GroupInvitation",
            $"Accepted group invitation to '{invitation.GroupName}'."));

        _stateBus.Publish(new SimulatorStateSignal(persona.Id, SignalCategory.GroupStateChanged, TargetChannelId: invitation.TargetGroupChannelId));
        _stateBus.Publish(new SimulatorStateSignal(persona.Id, SignalCategory.PendingHandshakeChanged));
        _toastService.ShowToast("Joined Group", $"Joined group '{invitation.GroupName}'.", ToastSeverity.Success);
        return true;
    }

    public async Task<bool> DeclineGroupInviteAsync(
        ISimulatedPersonaHost persona,
        InvitationId invitationId,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(persona);

        using var scope = persona.Services.CreateScope();
        var inviteRepo = scope.ServiceProvider.GetRequiredService<IGroupInvitationRepository>();
        var invitation = await inviteRepo.GetByIdAsync(invitationId, ct);
        if (invitation == null) return false;

        invitation.Decline((ulong)_virtualClock.UtcNow.ToUnixTimeMilliseconds());
        await inviteRepo.UpdateAsync(invitation, ct);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            persona.DisplayName,
            persona.Id,
            "GroupInvitation",
            $"Declined group invitation to '{invitation.GroupName}'."));

        _stateBus.Publish(new SimulatorStateSignal(persona.Id, SignalCategory.PendingHandshakeChanged));
        _toastService.ShowToast("Declined Invite", $"Declined invitation to '{invitation.GroupName}'.", ToastSeverity.Informational);
        return true;
    }

    public async Task<bool> SendGroupMessageAsync(
        ISimulatedPersonaHost sender,
        ChannelId channelId,
        string text,
        int? expireAfterSeconds = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (string.IsNullOrWhiteSpace(text)) return false;

        using var scope = sender.Services.CreateScope();
        var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();

        var cmd = new SendMessageCommand(
            channelId,
            sender.PublicIdentityId,
            null,
            DeviceId.Primary,
            DeliveryRoute.RelayedGroup,
            text,
            ExpireAfterSeconds: expireAfterSeconds);

        var sendResult = await chatService.SendMessageAsync(cmd, ct);
        if (sendResult.IsFailure)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Error,
                sender.DisplayName,
                sender.Id,
                "GroupMessaging",
                $"Failed to send group message: {sendResult.Error!.Description}"));
            return false;
        }

        await sender.FlushOutboxAsync(ct);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            sender.DisplayName,
            sender.Id,
            "GroupMessaging",
            $"Sent group message ({text.Length} chars) to channel {channelId}."));

        _stateBus.Publish(new SimulatorStateSignal(sender.Id, SignalCategory.ChatTranscriptChanged, TargetChannelId: channelId));
        return true;
    }

    public async Task<int> FloodGroupMessagesAsync(
        ISimulatedPersonaHost sender,
        ChannelId channelId,
        int count = 25,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (count <= 0) return 0;

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            sender.DisplayName,
            sender.Id,
            "GroupMessaging",
            $"Starting 25-message flood from {sender.DisplayName} to channel {channelId}..."));

        int sent = 0;
        for (int i = 1; i <= count; i++)
        {
            using var scope = sender.Services.CreateScope();
            var chatService = scope.ServiceProvider.GetRequiredService<IChatService>();
            var text = $"🌊 Flood Message #{i:D2} from {sender.DisplayName} ({_virtualClock.UtcNow:HH:mm:ss.fff})";

            var cmd = new SendMessageCommand(
                channelId,
                sender.PublicIdentityId,
                null,
                DeviceId.Primary,
                DeliveryRoute.RelayedGroup,
                text);

            var result = await chatService.SendMessageAsync(cmd, ct);
            if (result.IsSuccess)
            {
                sent++;
            }
        }

        await sender.FlushOutboxAsync(ct);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            sender.DisplayName,
            sender.Id,
            "GroupMessaging",
            $"Dispatched {sent}/{count} flood messages to channel {channelId}."));

        _stateBus.Publish(new SimulatorStateSignal(sender.Id, SignalCategory.ChatTranscriptChanged, TargetChannelId: channelId));
        _toastService.ShowToast("Group Flood Complete", $"Dispatched {sent} flood messages to channel.", ToastSeverity.Success);
        return sent;
    }

    public async Task<bool> AddGroupMemberAsync(
        ISimulatedPersonaHost admin,
        ISimulatedRelayHost relay,
        ChannelId channelId,
        ISimulatedPersonaHost newMember,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(newMember);

        using (var adminScope = admin.Services.CreateScope())
        {
            var channelRepo = adminScope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var groupChannel = await channelRepo.GetGroupByIdAsync(channelId, ct);
            if (groupChannel == null) throw new InvalidOperationException("Group channel not found.");

            var existingMemberIds = groupChannel.Members
                .Where(m => m.Id != admin.PublicIdentityId && m.Id != newMember.PublicIdentityId)
                .Select(m => m.Id)
                .ToList();

            var addResult = groupChannel.AddMember(admin.PublicIdentityId, newMember.PublicIdentityId, ChannelRole.Member, _virtualClock);
            if (addResult.IsFailure)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    admin.DisplayName,
                    admin.Id,
                    "GroupMutation",
                    $"Failed to add member: {addResult.Error!.Description}"));
                return false;
            }

            await channelRepo.SaveGroupAsync(groupChannel, ct);

            // Mutate Relay Ledger over gRPC (Zero in-process transport bypass)
            var channelFactory = adminScope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
            var grpcChannel = channelFactory.GetOrCreateChannel(relay.BoundEndpoint);
            var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(grpcChannel);

            var stateResponse = await client.FetchGroupStateAsync(new FetchGroupStateRequest
            {
                ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
                ZkPresentationProof = ByteString.Empty
            }, cancellationToken: ct);

            var newTokens = groupChannel.Members
                .Select(m => _worldState.FindPersonaHostByIdentity(m.Id)?.MailboxRoutingToken)
                .Where(t => t != null)
                .Cast<BlindedRoutingToken>()
                .ToHashSet();

            var zkEngine = new ZkgroupCryptographyService();
            Span<byte> challenge = stackalloc byte[32];
            var newBlob = EncryptedEntriesBlob.FromSpan(stateResponse.EncryptedRosterBlob.Span);
            RelayGroupLedger.ComputeMutationChallenge(newBlob, newTokens, challenge);
            var proof = zkEngine.GenerateGroupPresentation(stateResponse.CurrentEpoch, challenge, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

            var mutationReq = new CommitRosterMutationRequest
            {
                ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
                BaseEpoch = stateResponse.CurrentEpoch,
                NewRosterBlob = stateResponse.EncryptedRosterBlob,
                ZkPresentationProof = ByteString.CopyFrom(proof.Span)
            };

            foreach (var token in newTokens)
            {
                mutationReq.NewRoutingTokens.Add(ByteString.CopyFrom(token.ToByteArray()));
            }

            var mutationResp = await client.CommitRosterMutationAsync(mutationReq, cancellationToken: ct);
            if (!mutationResp.Committed)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    admin.DisplayName,
                    admin.Id,
                    "GroupMutation",
                    $"Relay rejected roster mutation for channel {channelId}."));
                return false;
            }

            // Notify existing members via MemberJoined SystemNotice over pairwise channels so their local GroupChannel roster updates
            var serializer = adminScope.ServiceProvider.GetRequiredService<IPayloadSerializer>();
            var payloadSender = adminScope.ServiceProvider.GetRequiredService<IPayloadSender>();
            var joinedNotice = new SystemNoticeDto(
                channelId,
                SystemNoticeType.MemberJoined,
                newMember.PublicIdentityId,
                (ulong)_virtualClock.UtcNow.ToUnixTimeMilliseconds());
            var noticeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(serializer, ChatPayloadType.SystemNotice, joinedNotice);

            foreach (var existingMemberId in existingMemberIds)
            {
                var existingHost = _worldState.FindPersonaHostByIdentity(existingMemberId);
                var directChannelId = ChannelIdUtility.DeterministicFor(admin.PublicIdentityId, existingMemberId);
                var route = existingHost?.HomeRelay != null ? DeliveryRoute.RelayedOneToOne : DeliveryRoute.DirectP2P;

                await payloadSender.SendPayloadAsync(new OutboundPayloadContext(
                    directChannelId,
                    admin.PublicIdentityId,
                    existingMemberId,
                    DeviceId.Primary,
                    AppId.Chat,
                    noticeBytes,
                    route), ct);
            }

            // Distribute sender keys to new member
            var keyDist = adminScope.ServiceProvider.GetRequiredService<IGroupKeyDistributionService>();
            await keyDist.DistributeSenderKeyAsync(channelId, admin.PublicIdentityId, DeviceId.Primary, [newMember.PublicIdentityId], ct);

            // Send invite to new member with current roster
            var inviteService = adminScope.ServiceProvider.GetRequiredService<GroupInvitationService>();
            ulong expiryMs = (ulong)_virtualClock.UtcNow.AddDays(7).ToUnixTimeMilliseconds();
            var allMemberIds = groupChannel.Members.Select(m => m.Id).ToList();
            var directInviteChannelId = ChannelIdUtility.DeterministicFor(admin.PublicIdentityId, newMember.PublicIdentityId);
            var inviteDto = new GroupInvitationDto(
                InvitationId.New(),
                channelId,
                groupChannel.Name,
                admin.PublicIdentityId,
                relay.BoundEndpoint.ToString(),
                expiryMs,
                allMemberIds);

            await inviteService.SendInvitationAsync(
                inviteDto,
                directInviteChannelId,
                newMember.PublicIdentityId,
                DeviceId.Primary,
                newMember.HomeRelay != null ? DeliveryRoute.RelayedOneToOne : DeliveryRoute.DirectP2P,
                ct);

            await admin.FlushOutboxAsync(ct);

            foreach (var existingMemberId in existingMemberIds)
            {
                var existingHost = _worldState.FindPersonaHostByIdentity(existingMemberId);
                if (existingHost != null)
                {
                    if (existingHost.HomeRelay != null)
                    {
                        await existingHost.DrainMailboxAsync(ct);
                    }

                    using var existingScope = existingHost.Services.CreateScope();
                    var existingKeyDist = existingScope.ServiceProvider.GetRequiredService<IGroupKeyDistributionService>();
                    await existingKeyDist.DistributeSenderKeyAsync(
                        channelId,
                        existingHost.PublicIdentityId,
                        DeviceId.Primary,
                        [newMember.PublicIdentityId],
                        ct);
                    await existingHost.FlushOutboxAsync(ct);
                }
            }

            if (newMember.HomeRelay != null)
            {
                await newMember.DrainMailboxAsync(ct);
            }

            if (newMember.AutoAcceptGroupInvites)
            {
                var capturedInvite = inviteDto;
                var capturedMember = newMember;
                UiTaskRunner.FireAndForget(
                    () => AcceptGroupInviteAsync(capturedMember, capturedInvite.InvitationId),
                    "AutoAcceptGroupInvite",
                    _logSink);
            }
        }

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            admin.DisplayName,
            admin.Id,
            "GroupMutation",
            $"Added {newMember.DisplayName} to group {channelId}."));

        _stateBus.Publish(new SimulatorStateSignal(admin.Id, SignalCategory.GroupStateChanged, TargetChannelId: channelId));
        _toastService.ShowToast("Member Added", $"Added {newMember.DisplayName} to group.", ToastSeverity.Success);
        return true;
    }

    public async Task<bool> EvictGroupMemberAsync(
        ISimulatedPersonaHost admin,
        ISimulatedRelayHost relay,
        ChannelId channelId,
        ISimulatedPersonaHost memberToEvict,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(memberToEvict);

        List<PublicIdentityId> remainingMemberIds;

        using (var adminScope = admin.Services.CreateScope())
        {
            var channelRepo = adminScope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var groupChannel = await channelRepo.GetGroupByIdAsync(channelId, ct);
            if (groupChannel == null) throw new InvalidOperationException("Group channel not found.");

            var removeResult = groupChannel.RemoveMember(admin.PublicIdentityId, memberToEvict.PublicIdentityId, _virtualClock);
            if (removeResult.IsFailure)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    admin.DisplayName,
                    admin.Id,
                    "GroupMutation",
                    $"Failed to evict member: {removeResult.Error!.Description}"));
                return false;
            }

            await channelRepo.SaveGroupAsync(groupChannel, ct);

            // Mutate Relay Ledger over gRPC (Epoch E -> E+1, revoking evicted routing token)
            var channelFactory = adminScope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
            var grpcChannel = channelFactory.GetOrCreateChannel(relay.BoundEndpoint);
            var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(grpcChannel);

            var stateResponse = await client.FetchGroupStateAsync(new FetchGroupStateRequest
            {
                ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
                ZkPresentationProof = ByteString.Empty
            }, cancellationToken: ct);

            var remainingTokens = groupChannel.Members
                .Where(m => m.Id != memberToEvict.PublicIdentityId)
                .Select(m => _worldState.FindPersonaHostByIdentity(m.Id)?.MailboxRoutingToken)
                .Where(t => t != null)
                .Cast<BlindedRoutingToken>()
                .ToHashSet();

            var zkEngine = new ZkgroupCryptographyService();
            Span<byte> challenge = stackalloc byte[32];
            var newBlob = EncryptedEntriesBlob.FromSpan(stateResponse.EncryptedRosterBlob.Span);
            RelayGroupLedger.ComputeMutationChallenge(newBlob, remainingTokens, challenge);
            var proof = zkEngine.GenerateGroupPresentation(stateResponse.CurrentEpoch, challenge, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

            var mutationReq = new CommitRosterMutationRequest
            {
                ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
                BaseEpoch = stateResponse.CurrentEpoch,
                NewRosterBlob = stateResponse.EncryptedRosterBlob,
                ZkPresentationProof = ByteString.CopyFrom(proof.Span)
            };

            foreach (var token in remainingTokens)
            {
                mutationReq.NewRoutingTokens.Add(ByteString.CopyFrom(token.ToByteArray()));
            }

            var mutationResp = await client.CommitRosterMutationAsync(mutationReq, cancellationToken: ct);
            if (!mutationResp.Committed)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    admin.DisplayName,
                    admin.Id,
                    "GroupMutation",
                    $"Relay rejected eviction mutation for channel {channelId}."));
                return false;
            }

            remainingMemberIds = groupChannel.Members
                .Where(m => m.Id != admin.PublicIdentityId && m.Id != memberToEvict.PublicIdentityId)
                .Select(m => m.Id)
                .ToList();

            // Dispatch MemberLeft SystemNotice over pairwise 1:1 channels to remaining members and evicted member
            var serializer = adminScope.ServiceProvider.GetRequiredService<IPayloadSerializer>();
            var payloadSender = adminScope.ServiceProvider.GetRequiredService<IPayloadSender>();
            var evictionNotice = new SystemNoticeDto(
                channelId,
                SystemNoticeType.MemberLeft,
                memberToEvict.PublicIdentityId,
                (ulong)_virtualClock.UtcNow.ToUnixTimeMilliseconds());
            var noticeBytes = SteppedPadding.CreatePaddedEnvelopeBytes(serializer, ChatPayloadType.SystemNotice, evictionNotice);

            var noticeTargetIds = remainingMemberIds.Append(memberToEvict.PublicIdentityId).ToList();
            foreach (var targetId in noticeTargetIds)
            {
                var targetHost = _worldState.FindPersonaHostByIdentity(targetId);
                var directChannelId = ChannelIdUtility.DeterministicFor(admin.PublicIdentityId, targetId);
                var route = targetHost?.HomeRelay != null ? DeliveryRoute.RelayedOneToOne : DeliveryRoute.DirectP2P;

                await payloadSender.SendPayloadAsync(new OutboundPayloadContext(
                    directChannelId,
                    admin.PublicIdentityId,
                    targetId,
                    DeviceId.Primary,
                    AppId.Chat,
                    noticeBytes,
                    route), ct);
            }

            // Forward Secrecy: Rotate Admin's sender key and distribute ONLY to remaining members
            await RotateAndDistributeHostSenderKeyAsync(adminScope.ServiceProvider, channelId, admin.PublicIdentityId, remainingMemberIds, ct);

            await admin.FlushOutboxAsync(ct);

            foreach (var targetId in noticeTargetIds)
            {
                var targetHost = _worldState.FindPersonaHostByIdentity(targetId);
                if (targetHost?.HomeRelay != null)
                {
                    await targetHost.DrainMailboxAsync(ct);
                }
            }
        }

        // Forward Secrecy for all remaining members: each remaining member also rotates their SenderKey and distributes to active peers
        var allActiveMemberIds = remainingMemberIds.Append(admin.PublicIdentityId).ToList();
        foreach (var memberId in remainingMemberIds)
        {
            var memberHost = _worldState.FindPersonaHostByIdentity(memberId);
            if (memberHost == null) continue;

            var peerIds = allActiveMemberIds.Where(id => id != memberId).ToList();
            using (var memberScope = memberHost.Services.CreateScope())
            {
                await RotateAndDistributeHostSenderKeyAsync(memberScope.ServiceProvider, channelId, memberId, peerIds, ct);
            }

            await memberHost.FlushOutboxAsync(ct);

            foreach (var peerId in peerIds)
            {
                var peerHost = _worldState.FindPersonaHostByIdentity(peerId);
                if (peerHost?.HomeRelay != null)
                {
                    await peerHost.DrainMailboxAsync(ct);
                }
            }

            _stateBus.Publish(new SimulatorStateSignal(memberHost.Id, SignalCategory.GroupStateChanged, TargetChannelId: channelId));
        }

        // Unsubscribe evicted member from group stream
        UnsubscribePersonaFromGroup(memberToEvict, channelId);

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            admin.DisplayName,
            admin.Id,
            "GroupMutation",
            $"Evicted {memberToEvict.DisplayName} from group {channelId}. Forward secrecy SenderKey rotated for remaining members."));

        _stateBus.Publish(new SimulatorStateSignal(admin.Id, SignalCategory.GroupStateChanged, TargetChannelId: channelId));
        _stateBus.Publish(new SimulatorStateSignal(memberToEvict.Id, SignalCategory.GroupStateChanged, TargetChannelId: channelId));
        _toastService.ShowToast("Member Evicted", $"Evicted {memberToEvict.DisplayName} from group.", ToastSeverity.Informational);
        return true;
    }

    private static async Task RotateAndDistributeHostSenderKeyAsync(
        IServiceProvider serviceProvider,
        ChannelId channelId,
        PublicIdentityId authorId,
        IReadOnlyCollection<PublicIdentityId> recipientMemberIds,
        CancellationToken ct)
    {
        var senderKeyRepo = serviceProvider.GetRequiredService<IGroupSenderKeyRepository>();
        var oldRatchet = await senderKeyRepo.GetSenderKeyRatchetAsync(channelId, authorId, DeviceId.Primary, ct);
        uint nextKeyId = oldRatchet != null ? oldRatchet.KeyId + 1 : 1;

        var crypto = new SignalCryptoEngine();
        var (newSigningPriv, newSigningPub) = crypto.GenerateEphemeralKeyPair();
        var chainBytes = new byte[32];
        RandomNumberGenerator.Fill(chainBytes);
        using var newChain = ChainKey.FromSpan(chainBytes);

        var newRatchet = new GroupSenderKeyRatchet(
            channelId,
            authorId,
            DeviceId.Primary,
            newChain,
            initialIteration: 0,
            keyId: nextKeyId,
            signingPrivateKey: newSigningPriv,
            authorSigningPublicKey: IdentityKey.FromSpan(newSigningPub.Span));

        await senderKeyRepo.SaveSenderKeyRatchetAsync(newRatchet, ct);

        if (recipientMemberIds.Count > 0)
        {
            var keyDist = serviceProvider.GetRequiredService<IGroupKeyDistributionService>();
            await keyDist.DistributeSenderKeyAsync(channelId, authorId, DeviceId.Primary, recipientMemberIds, ct);
        }
    }

    public async Task<bool> RenameGroupAsync(
        ISimulatedPersonaHost admin,
        ISimulatedRelayHost relay,
        ChannelId channelId,
        string newName,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentNullException.ThrowIfNull(relay);
        if (string.IsNullOrWhiteSpace(newName)) return false;

        using (var adminScope = admin.Services.CreateScope())
        {
            var channelRepo = adminScope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var groupChannel = await channelRepo.GetGroupByIdAsync(channelId, ct);
            if (groupChannel == null) throw new InvalidOperationException("Group channel not found.");

            var renameResult = groupChannel.RenameChannel(admin.PublicIdentityId, newName, _virtualClock);
            if (renameResult.IsFailure)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    admin.DisplayName,
                    admin.Id,
                    "GroupMutation",
                    $"Failed to rename group: {renameResult.Error!.Description}"));
                return false;
            }

            await channelRepo.SaveGroupAsync(groupChannel, ct);

            // Mutate Relay Ledger over gRPC (advancing epoch)
            var channelFactory = adminScope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
            var grpcChannel = channelFactory.GetOrCreateChannel(relay.BoundEndpoint);
            var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(grpcChannel);

            var stateResponse = await client.FetchGroupStateAsync(new FetchGroupStateRequest
            {
                ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
                ZkPresentationProof = ByteString.Empty
            }, cancellationToken: ct);

            var activeTokens = groupChannel.Members
                .Select(m => _worldState.FindPersonaHostByIdentity(m.Id)?.MailboxRoutingToken)
                .Where(t => t != null)
                .Cast<BlindedRoutingToken>()
                .ToHashSet();

            var zkEngine = new ZkgroupCryptographyService();
            Span<byte> challenge = stackalloc byte[32];
            var newBlob = EncryptedEntriesBlob.FromSpan(stateResponse.EncryptedRosterBlob.Span);
            RelayGroupLedger.ComputeMutationChallenge(newBlob, activeTokens, challenge);
            var proof = zkEngine.GenerateGroupPresentation(stateResponse.CurrentEpoch, challenge, ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty);

            var mutationReq = new CommitRosterMutationRequest
            {
                ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
                BaseEpoch = stateResponse.CurrentEpoch,
                NewRosterBlob = stateResponse.EncryptedRosterBlob,
                ZkPresentationProof = ByteString.CopyFrom(proof.Span)
            };

            foreach (var token in activeTokens)
            {
                mutationReq.NewRoutingTokens.Add(ByteString.CopyFrom(token.ToByteArray()));
            }

            var mutationResp = await client.CommitRosterMutationAsync(mutationReq, cancellationToken: ct);
            if (!mutationResp.Committed)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Error,
                    admin.DisplayName,
                    admin.Id,
                    "GroupMutation",
                    $"Relay rejected rename mutation for channel {channelId}."));
                return false;
            }
        }

        _logSink.Log(new SimulatorLogEntry(
            DateTimeOffset.UtcNow,
            LogLevel.Information,
            admin.DisplayName,
            admin.Id,
            "GroupMutation",
            $"Renamed group {channelId} to '{newName}'."));

        _stateBus.Publish(new SimulatorStateSignal(admin.Id, SignalCategory.GroupStateChanged, TargetChannelId: channelId));
        _toastService.ShowToast("Group Renamed", $"Renamed group to '{newName}'.", ToastSeverity.Success);
        return true;
    }

    public async Task<IReadOnlyList<ChatMessageItemViewModel>> GetGroupTranscriptAsync(
        ISimulatedPersonaHost host,
        ChannelId channelId,
        CancellationToken ct = default)
    {
        if (host == null) return Array.Empty<ChatMessageItemViewModel>();
        try
        {
            using var scope = host.Services.CreateScope();
            var chatQuery = scope.ServiceProvider.GetRequiredService<ISimulatorChatQueryService>();
            var messages = await chatQuery.GetTranscriptAsync(channelId, ct);

            return messages.Select(m =>
            {
                var isOutgoing = m.AuthorId == host.PublicIdentityId;
                var authorPersona = _worldState.FindPersonaHostByIdentity(m.AuthorId);
                var authorName = isOutgoing ? host.DisplayName : (authorPersona?.DisplayName ?? m.AuthorId.ToString()[..8]);
                var timeText = DateTimeOffset.FromUnixTimeMilliseconds((long)m.TimestampUnixMs).ToLocalTime().ToString("HH:mm:ss");

                var isRead = m.ReadAtUnixMs.HasValue;
                var isDelivered = m.DeliveredAtUnixMs.HasValue || isRead;
                var statusText = isOutgoing ? (isDelivered ? "✓✓" : "✓") : string.Empty;

                var reactionsSummary = m.Reactions.Count > 0
                    ? string.Join(" ", m.Reactions.GroupBy(r => r).Select(g => $"{g.Key} {g.Count()}"))
                    : string.Empty;

                return new ChatMessageItemViewModel
                {
                    MessageId = m.MessageId.ToString(),
                    SenderName = authorName,
                    Content = m.Content,
                    TimestampText = timeText,
                    IsOutgoing = isOutgoing,
                    StatusText = statusText,
                    IsEdited = m.EditedAtUnixMs.HasValue,
                    IsDelivered = isDelivered,
                    IsRead = isRead,
                    ReactionsSummary = reactionsSummary
                };
            }).ToList();
        }
        catch (Exception ex)
        {
            _logSink.Log(new SimulatorLogEntry(
                DateTimeOffset.UtcNow,
                LogLevel.Warning,
                host.DisplayName,
                host.Id,
                "GroupMessaging",
                $"Error fetching group chat transcript: {ex.Message}"));
            return Array.Empty<ChatMessageItemViewModel>();
        }
    }

    public async Task<(string Name, ulong CurrentEpoch, IReadOnlyList<(PublicIdentityId IdentityId, string DisplayName, ChannelRole Role)> Members)?> GetGroupDetailsAsync(
        ChannelId channelId,
        CancellationToken ct = default)
    {
        ISimulatedPersonaHost? personaHost = null;
        GroupChannel? group = null;

        foreach (var host in _worldState.GetAllPersonaHosts())
        {
            using var scope = host.Services.CreateScope();
            var channelRepo = scope.ServiceProvider.GetRequiredService<IChannelRepository>();
            var candidate = await channelRepo.GetGroupByIdAsync(channelId, ct);
            if (candidate != null)
            {
                if (candidate.Members.Any(m => m.Id == host.PublicIdentityId && m.Role == ChannelRole.Admin))
                {
                    personaHost = host;
                    group = candidate;
                    break;
                }
                personaHost ??= host;
                group ??= candidate;
            }
        }

        if (personaHost == null || group == null)
        {
            return null;
        }

        var memberList = group.Members.Select(m =>
        {
            var memberHost = _worldState.FindPersonaHostByIdentity(m.Id);
            var name = memberHost?.DisplayName ?? m.Id.ToString()[..8];
            return (m.Id, name, m.Role);
        }).ToList();

        ulong epoch = 0;
        if (_groupRelays.TryGetValue(channelId, out var relayUri))
        {
            try
            {
                using var scope = personaHost.Services.CreateScope();
                var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
                var grpcChannel = channelFactory.GetOrCreateChannel(relayUri);
                var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(grpcChannel);
                var state = await client.FetchGroupStateAsync(new FetchGroupStateRequest
                {
                    ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
                    ZkPresentationProof = ByteString.Empty
                }, cancellationToken: ct);
                epoch = state.CurrentEpoch;
            }
            catch (Exception ex)
            {
                _logSink.Log(new SimulatorLogEntry(
                    DateTimeOffset.UtcNow,
                    LogLevel.Debug,
                    personaHost.DisplayName,
                    personaHost.Id,
                    "GroupQuery",
                    $"Failed to query group epoch via gRPC: {ex.Message}"));
            }
        }

        return (group.Name, epoch, memberList);
    }

    public void SubscribePersonaToGroup(ISimulatedPersonaHost persona, ChannelId channelId, Uri relayUri)
    {
        var key = (persona.Id, channelId);
        if (_streamSubscriptions.ContainsKey(key)) return;

        var cts = new CancellationTokenSource();
        if (!_streamSubscriptions.TryAdd(key, cts))
        {
            cts.Dispose();
            return;
        }

        _ = Task.Run(async () =>
        {
            var token = cts.Token;
            while (!token.IsCancellationRequested)
            {
                while (!token.IsCancellationRequested && !persona.IsListening.CurrentValue)
                {
                    try
                    {
                        await Task.Delay(100, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        return;
                    }
                }

                if (token.IsCancellationRequested) break;

                using var listenCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var listeningSub = persona.IsListening.Subscribe(listening =>
                {
                    if (!listening)
                    {
                        listenCts.Cancel();
                    }
                });

                try
                {
                    using var scope = persona.Services.CreateScope();
                    var channelFactory = scope.ServiceProvider.GetRequiredService<IPeerGrpcChannelFactory>();
                    var grpcChannel = channelFactory.GetOrCreateChannel(relayUri);
                    var client = new AnonymousGroupRelayService.AnonymousGroupRelayServiceClient(grpcChannel);

                    var request = new GroupStreamClientMessage
                    {
                        ConversationId = ByteString.CopyFrom(channelId.ToByteArray()),
                        CurrentEpoch = 0,
                        ZkPresentationProof = ByteString.Empty,
                        SubscriberRoutingToken = ByteString.CopyFrom(persona.MailboxRoutingToken.ToByteArray())
                    };

                    using var call = client.SubscribeGroupStream(request, cancellationToken: listenCts.Token);

                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Debug,
                        persona.DisplayName,
                        persona.Id,
                        "GroupStream",
                        $"Connected to group broadcast stream on {relayUri} for {channelId}."));

                    while (await call.ResponseStream.MoveNext(listenCts.Token).ConfigureAwait(false))
                    {
                        var msg = call.ResponseStream.Current;
                        if (msg.PayloadCase == GroupStreamServerMessage.PayloadOneofCase.Broadcast && msg.Broadcast != null)
                        {
                            var ciphertextBytes = msg.Broadcast.Ciphertext.ToByteArray();
                            var decodeResult = SealedWireFraming.Decode(ciphertextBytes);
                            if (decodeResult.IsSuccess)
                            {
                                var (_, _, senderId, _, _) = decodeResult.Value;
                                if (senderId == persona.PublicIdentityId)
                                {
                                    // Skip self-broadcast echo
                                    continue;
                                }
                            }

                            await persona.IngestGroupBroadcastAsync(ciphertextBytes, listenCts.Token).ConfigureAwait(false);
                        }
                        else if (msg.PayloadCase == GroupStreamServerMessage.PayloadOneofCase.Mutation && msg.Mutation != null)
                        {
                            _logSink.Log(new SimulatorLogEntry(
                                DateTimeOffset.UtcNow,
                                LogLevel.Information,
                                persona.DisplayName,
                                persona.Id,
                                "GroupStream",
                                $"Received epoch mutation notification (New Epoch: {msg.Mutation.NewEpoch}) for channel {channelId}."));

                            _stateBus.Publish(new SimulatorStateSignal(persona.Id, SignalCategory.GroupStateChanged, TargetChannelId: channelId));
                        }
                    }
                }
                catch (OperationCanceledException) when (listenCts.IsCancellationRequested && !token.IsCancellationRequested)
                {
                    // Host went offline / muted; loop back to wait for listening == true
                }
                catch (RpcException ex) when (listenCts.IsCancellationRequested || ex.StatusCode == StatusCode.Cancelled)
                {
                    // Host went offline or stream cancelled normally
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) break;

                    _logSink.Log(new SimulatorLogEntry(
                        DateTimeOffset.UtcNow,
                        LogLevel.Warning,
                        persona.DisplayName,
                        persona.Id,
                        "GroupStream",
                        $"Group stream connection error: {ex.Message}. Reconnecting in 500ms..."));

                    try
                    {
                        await Task.Delay(500, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }, cts.Token);
    }

    public void UnsubscribePersonaFromGroup(ISimulatedPersonaHost persona, ChannelId channelId)
    {
        var key = (persona.Id, channelId);
        if (_streamSubscriptions.TryRemove(key, out var cts))
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // CTS already disposed concurrently; safe to ignore during teardown.
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var cts in _streamSubscriptions.Values)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch (ObjectDisposedException)
            {
                // CTS already disposed concurrently; safe to ignore during teardown.
            }
        }
        _streamSubscriptions.Clear();
    }
}
