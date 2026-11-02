using System.Buffers;
using Google.Protobuf;
using Percolator.Apps.Chat.Features.GroupInvitations;
using Percolator.Apps.Chat.Features.Media;
using Percolator.Apps.Chat.Features.Messaging;
using Percolator.Apps.Chat.Features.Profiles;
using Percolator.Apps.Chat.Features.Reactions;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Channels.ValueObjects;
using Percolator.Domain.Common;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure2.Protos;
using Percolator.PluginSdk;

namespace Percolator.Infrastructure2.Serialization;

public sealed class ProtobufPayloadSerializer : IPayloadSerializer
{
    private const int Sha256ByteLength = 32;

    public void Serialize<T>(T payload, IBufferWriter<byte> writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        IMessage message = ToProtoMessage(payload);
        message.WriteTo(writer);
    }

    public ReadOnlyMemory<byte> Serialize<T>(T payload)
    {
        IMessage message = ToProtoMessage(payload);
        return message.ToByteArray();
    }

    public DomainResult<T> Deserialize<T>(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            return DomainResult<T>.Failure(new DomainError("Serialization.EmptyPayload", "Cannot deserialize empty payload."));
        }

        try
        {
            object result = typeof(T) switch
            {
                Type t when t == typeof(TransferManifest) => FromManifestProto(TransferManifestProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(FileTransferOfferPayload) => FromOfferProto(FileTransferOfferProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(FileTransferAcceptPayload) => FromAcceptProto(FileTransferAcceptProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(FileTransferCancelPayload) => FromCancelProto(FileTransferCancelProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(ChatPayloadEnvelopeDto) => FromEnvelopeProto(ChatPayloadEnvelopeProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(ChatMessageDto) => FromChatMessageProto(ChatMessageProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(MessageEditDto) => FromEditProto(MessageEditProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(MessageTombstoneDto) => FromTombstoneProto(MessageTombstoneProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(ReactionUpdateDto) => FromReactionProto(ReactionUpdateProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(ReadReceiptDto) => FromReadReceiptProto(ReadReceiptProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(SystemNoticeDto) => FromNoticeProto(SystemNoticeProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(GroupInvitationDto) => FromInviteProto(GroupInvitationProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(GroupInvitationResponseDto) => FromInviteResponseProto(GroupInvitationResponseProto.Parser.ParseFrom(data.Span)),
                Type t when t == typeof(ProfileCapabilityDto) => FromProfileCapabilityProto(ProfileCapabilityProto.Parser.ParseFrom(data.Span)),
                _ => throw new NotSupportedException($"Payload type '{typeof(T).FullName}' is not registered in ProtobufPayloadSerializer.")
            };

            return DomainResult<T>.Success((T)result);
        }
        catch (Exception ex)
        {
            return DomainResult<T>.Failure(new DomainError("Serialization.DeserializationFailed", ex.Message));
        }
    }

    private static IMessage ToProtoMessage<T>(T payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        return payload switch
        {
            TransferManifest manifest => ToManifestProto(manifest),
            FileTransferOfferPayload offer => ToOfferProto(offer),
            FileTransferAcceptPayload accept => ToAcceptProto(accept),
            FileTransferCancelPayload cancel => ToCancelProto(cancel),
            ChatPayloadEnvelopeDto envelope => ToEnvelopeProto(envelope),
            ChatMessageDto msg => ToChatMessageProto(msg),
            MessageEditDto edit => ToEditProto(edit),
            MessageTombstoneDto tomb => ToTombstoneProto(tomb),
            ReactionUpdateDto rx => ToReactionProto(rx),
            ReadReceiptDto rr => ToReadReceiptProto(rr),
            SystemNoticeDto notice => ToNoticeProto(notice),
            GroupInvitationDto invite => ToInviteProto(invite),
            GroupInvitationResponseDto inviteResp => ToInviteResponseProto(inviteResp),
            ProfileCapabilityDto profCap => ToProfileCapabilityProto(profCap),
            _ => throw new NotSupportedException($"Payload type '{typeof(T).FullName}' is not registered in ProtobufPayloadSerializer.")
        };
    }

    private static TransferManifestProto ToManifestProto(TransferManifest manifest)
    {
        byte[] concatenatedLeaves = new byte[manifest.MerkleTree.Leaves.Count * Sha256ByteLength];
        for (int i = 0; i < manifest.MerkleTree.Leaves.Count; i++)
        {
            manifest.MerkleTree.Leaves[i].Span.CopyTo(concatenatedLeaves.AsSpan(i * Sha256ByteLength, Sha256ByteLength));
        }

        TransferManifestProto proto = new()
        {
            ManifestId = ByteString.CopyFrom(manifest.ManifestId.ToByteArray()),
            RootName = manifest.RootName.Value,
            TotalSizeBytes = manifest.TotalSize.Bytes,
            ChunkSizeBytes = manifest.ChunkSize.Bytes,
            MerkleRoot = ByteString.CopyFrom(manifest.MerkleTree.Root.Span),
            ConcatenatedLeaves = ByteString.CopyFrom(concatenatedLeaves)
        };

        foreach (FileEntry file in manifest.Files)
        {
            proto.Files.Add(new FileEntryProto
            {
                Index = file.Index.Value,
                RelativePath = file.RelativePath.Value,
                FileSizeBytes = file.FileSize.Bytes,
                StartOffset = file.StartOffset.Offset,
                LastModifiedUnixSeconds = file.LastModifiedUtc.UnixSeconds
            });
        }

        return proto;
    }

    private static TransferManifest FromManifestProto(TransferManifestProto proto)
    {
        TransferSessionId manifestId = TransferSessionId.FromBytes(proto.ManifestId.Span);
        RootBundleName rootName = RootBundleName.Parse(proto.RootName);
        ChunkSizeBytes chunkSize = ChunkSizeBytes.FromBytes(proto.ChunkSizeBytes);
        MerkleRoot merkleRoot = MerkleRoot.FromSpan(proto.MerkleRoot.Span);

        ReadOnlySpan<byte> leavesSpan = proto.ConcatenatedLeaves.Span;
        if (leavesSpan.Length == 0 || leavesSpan.Length % Sha256ByteLength != 0)
            throw new InvalidOperationException("Manifest concatenated leaves byte length must be a positive multiple of 32.");

        int leafCount = leavesSpan.Length / Sha256ByteLength;
        List<ChunkHash> leaves = new(leafCount);
        for (int i = 0; i < leafCount; i++)
        {
            leaves.Add(ChunkHash.FromSpan(leavesSpan.Slice(i * Sha256ByteLength, Sha256ByteLength)));
        }

        MerkleTreeIndex merkleTree = MerkleTreeIndex.Create(merkleRoot, leaves);

        List<FileEntry> files = new(proto.Files.Count);
        foreach (FileEntryProto f in proto.Files)
        {
            files.Add(new FileEntry(
                FileIndex.FromInt(f.Index),
                SanitizedRelativePath.Parse(f.RelativePath),
                FileSizeBytes.FromBytes(f.FileSizeBytes),
                BundleByteOffset.FromOffset(f.StartOffset),
                FileLastModifiedUtc.FromUnixSeconds(f.LastModifiedUnixSeconds)));
        }

        return TransferManifest.Create(manifestId, rootName, chunkSize, merkleTree, files);
    }

    private static FileTransferOfferProto ToOfferProto(FileTransferOfferPayload offer)
    {
        ManifestDistributionProto distProto = new()
        {
            Kind = (uint)offer.Manifest.Kind
        };

        if (offer.Manifest.Kind == ManifestDistributionKind.Inline && offer.Manifest.InlineData != null)
        {
            distProto.InlineManifestData = ByteString.CopyFrom(offer.Manifest.InlineData.Span);
        }
        else if (offer.Manifest.Kind == ManifestDistributionKind.RemoteBlob && offer.Manifest.BlobDescriptor != null)
        {
            distProto.RemoteBlob = new ManifestBlobDescriptorProto
            {
                BlobId = offer.Manifest.BlobDescriptor.BlobId.HexDigest,
                CiphertextSha256 = ByteString.CopyFrom(offer.Manifest.BlobDescriptor.CiphertextSha256.Span),
                PlaintextSizeBytes = offer.Manifest.BlobDescriptor.PlaintextSizeBytes.Bytes,
                RelayEndpointUri = offer.Manifest.BlobDescriptor.RelayEndpointUri.Value
            };
        }

        FileTransferOfferProto proto = new()
        {
            TransferSessionId = ByteString.CopyFrom(offer.TransferSessionId.ToByteArray()),
            Summary = new TransferBundleSummaryProto
            {
                RootName = offer.Summary.RootName.Value,
                TotalSizeBytes = offer.Summary.TotalSize.Bytes,
                TotalFiles = offer.Summary.TotalFiles.Count,
                ChunkSizeBytes = offer.Summary.ChunkSizeBytes.Bytes,
                TotalChunks = offer.Summary.TotalChunks.Count,
                MerkleRoot = ByteString.CopyFrom(offer.Summary.MerkleRoot.Span)
            },
            Manifest = distProto,
            SessionEncryptionKey = ByteString.CopyFrom(offer.SessionEncryptionKey.Span),
            BaseNonce = ByteString.CopyFrom(offer.BaseNonce.Span),
            StreamAuthToken = ByteString.CopyFrom(offer.StreamAuthToken.Span)
        };

        if (offer.RelayRendezvous != null)
        {
            proto.RelayRendezvous = new RelayRendezvousDescriptorProto
            {
                EndpointUri = offer.RelayRendezvous.EndpointUri.Value,
                Token = ByteString.CopyFrom(offer.RelayRendezvous.Token.Span)
            };
        }

        foreach (DirectEndpointCandidate candidate in offer.DirectCandidates)
        {
            proto.DirectCandidates.Add(new DirectEndpointCandidateProto
            {
                Host = candidate.Host.Value,
                Port = candidate.Port.Value,
                Kind = (uint)candidate.Kind
            });
        }

        return proto;
    }

    private static FileTransferOfferPayload FromOfferProto(FileTransferOfferProto proto)
    {
        TransferSessionId sessionId = TransferSessionId.FromBytes(proto.TransferSessionId.Span);

        TransferBundleSummary summary = TransferBundleSummary.Create(
            RootBundleName.Parse(proto.Summary.RootName),
            TotalBundleBytes.FromBytes(proto.Summary.TotalSizeBytes),
            FileCount.FromInt(proto.Summary.TotalFiles),
            ChunkSizeBytes.FromBytes(proto.Summary.ChunkSizeBytes),
            ChunkCount.FromInt(proto.Summary.TotalChunks),
            MerkleRoot.FromSpan(proto.Summary.MerkleRoot.Span));

        ManifestDistributionKind kind = (ManifestDistributionKind)proto.Manifest.Kind;
        ManifestDistribution manifestDist = kind switch
        {
            ManifestDistributionKind.Inline => ManifestDistribution.FromInline(
                InlineManifestData.FromSpan(proto.Manifest.InlineManifestData.Span)),
            ManifestDistributionKind.RemoteBlob => ManifestDistribution.FromBlob(new ManifestBlobDescriptor(
                new BlobId(proto.Manifest.RemoteBlob.BlobId),
                ChunkHash.FromSpan(proto.Manifest.RemoteBlob.CiphertextSha256.Span),
                TotalBundleBytes.FromBytes(proto.Manifest.RemoteBlob.PlaintextSizeBytes),
                RelayPipeEndpointUri.Parse(proto.Manifest.RemoteBlob.RelayEndpointUri))),
            _ => throw new InvalidOperationException($"Unknown ManifestDistributionKind: {proto.Manifest.Kind}")
        };

        TransferKey sessionKey = TransferKey.FromSpan(proto.SessionEncryptionKey.Span);
        BaseNonce baseNonce = BaseNonce.FromSpan(proto.BaseNonce.Span);
        StreamAuthToken streamAuthToken = StreamAuthToken.FromSpan(proto.StreamAuthToken.Span);

        RelayRendezvousDescriptor? relay = proto.RelayRendezvous != null
            ? new RelayRendezvousDescriptor(
                RelayPipeEndpointUri.Parse(proto.RelayRendezvous.EndpointUri),
                RelayPipeToken.FromSpan(proto.RelayRendezvous.Token.Span))
            : null;

        List<DirectEndpointCandidate> candidates = new(proto.DirectCandidates.Count);
        foreach (DirectEndpointCandidateProto c in proto.DirectCandidates)
        {
            candidates.Add(new DirectEndpointCandidate(
                EndpointHost.Parse(c.Host),
                PortNumber.FromInt(c.Port),
                (DirectCandidateKind)c.Kind));
        }

        return new FileTransferOfferPayload(
            sessionId,
            summary,
            manifestDist,
            sessionKey,
            baseNonce,
            streamAuthToken,
            relay,
            candidates);
    }

    private static FileTransferAcceptProto ToAcceptProto(FileTransferAcceptPayload accept)
    {
        FileTransferAcceptProto proto = new()
        {
            TransferSessionId = ByteString.CopyFrom(accept.TransferSessionId.ToByteArray()),
            RoutingPreference = (uint)accept.RoutingPreference,
            TotalChunks = accept.ResumptionBitfield.TotalChunks.Count,
            ResumptionBitfield = ByteString.CopyFrom(accept.ResumptionBitfield.ToByteArray())
        };

        foreach (DirectEndpointCandidate candidate in accept.ReceiverCandidates)
        {
            proto.ReceiverCandidates.Add(new DirectEndpointCandidateProto
            {
                Host = candidate.Host.Value,
                Port = candidate.Port.Value,
                Kind = (uint)candidate.Kind
            });
        }

        return proto;
    }

    private static FileTransferAcceptPayload FromAcceptProto(FileTransferAcceptProto proto)
    {
        TransferSessionId sessionId = TransferSessionId.FromBytes(proto.TransferSessionId.Span);
        TransportRoutingPreference routing = (TransportRoutingPreference)proto.RoutingPreference;

        List<DirectEndpointCandidate> candidates = new(proto.ReceiverCandidates.Count);
        foreach (DirectEndpointCandidateProto c in proto.ReceiverCandidates)
        {
            candidates.Add(new DirectEndpointCandidate(
                EndpointHost.Parse(c.Host),
                PortNumber.FromInt(c.Port),
                (DirectCandidateKind)c.Kind));
        }

        Bitfield bitfield = Bitfield.FromBytes(proto.ResumptionBitfield.Span, ChunkCount.FromInt(proto.TotalChunks));
        return new FileTransferAcceptPayload(sessionId, candidates, routing, bitfield);
    }

    private static FileTransferCancelProto ToCancelProto(FileTransferCancelPayload cancel) =>
        new()
        {
            TransferSessionId = ByteString.CopyFrom(cancel.TransferSessionId.ToByteArray()),
            Reason = cancel.Reason.Value
        };

    private static FileTransferCancelPayload FromCancelProto(FileTransferCancelProto proto) =>
        new(
            TransferSessionId.FromBytes(proto.TransferSessionId.Span),
            TransferTerminationReason.FromText(proto.Reason));

    // Chat DTO Serialization

    private static ChatPayloadEnvelopeProto ToEnvelopeProto(ChatPayloadEnvelopeDto dto) =>
        new()
        {
            Type = (int)dto.Type,
            Data = ByteString.CopyFrom(dto.Data.Span),
            Padding = ByteString.CopyFrom(dto.Padding.Span)
        };

    private static ChatPayloadEnvelopeDto FromEnvelopeProto(ChatPayloadEnvelopeProto proto) =>
        new(
            (ChatPayloadType)proto.Type,
            proto.Data.ToByteArray(),
            proto.Padding.ToByteArray());

    private static ChatMessageProto ToChatMessageProto(ChatMessageDto dto)
    {
        ChatMessageProto proto = new()
        {
            Id = ByteString.CopyFrom(dto.Id.ToByteArray()),
            ChannelId = ByteString.CopyFrom(dto.ChannelId.ToByteArray()),
            AuthorId = ByteString.CopyFrom(dto.AuthorId.ToByteArray()),
            TimestampUnixMs = dto.TimestampUnixMs,
            Content = dto.Content ?? string.Empty,
        };

        if (dto.ExpireAfterSeconds.HasValue)
        {
            proto.ExpireAfterSeconds = dto.ExpireAfterSeconds.Value;
        }

        if (dto.EditedAtUnixMs.HasValue)
        {
            proto.EditedAtUnixMs = dto.EditedAtUnixMs.Value;
        }

        if (dto.ReplyContext != null)
        {
            proto.ReplyContext = new ReplyContextProto
            {
                ParentPayloadId = ByteString.CopyFrom(dto.ReplyContext.ParentPayloadId.ToByteArray()),
                ParentAuthorId = ByteString.CopyFrom(dto.ReplyContext.ParentAuthorId.ToByteArray())
            };
        }

        if (dto.Mentions != null)
        {
            foreach (MentionDto m in dto.Mentions)
            {
                proto.Mentions.Add(new MentionProto
                {
                    IdentityId = ByteString.CopyFrom(m.IdentityId.ToByteArray()),
                    StartOffset = m.StartOffset,
                    Length = m.Length
                });
            }
        }

        if (dto.LinkPreview != null)
        {
            proto.LinkPreview = new LinkPreviewProto
            {
                Title = dto.LinkPreview.Title ?? string.Empty,
                Description = dto.LinkPreview.Description ?? string.Empty,
                CanonicalUrl = dto.LinkPreview.CanonicalUrl ?? string.Empty,
                ThumbnailBlob = dto.LinkPreview.ThumbnailBlob != null ? ToBlobRefProto(dto.LinkPreview.ThumbnailBlob) : null
            };
        }

        if (dto.Attachments != null)
        {
            foreach (MediaAttachmentDto a in dto.Attachments)
            {
                MediaAttachmentProto ap = new()
                {
                    BlobId = a.BlobId.HexDigest,
                    ContentType = a.ContentType ?? string.Empty,
                    ByteSize = a.ByteSize,
                    OutOfBandReference = a.OutOfBandReference != null ? ToBlobRefProto(a.OutOfBandReference) : null
                };

                if (a.FileName != null)
                {
                    ap.FileName = a.FileName;
                }

                if (a.Width.HasValue)
                {
                    ap.Width = a.Width.Value;
                }

                if (a.Height.HasValue)
                {
                    ap.Height = a.Height.Value;
                }

                if (a.Waveform != null)
                {
                    ap.Waveform = ByteString.CopyFrom(a.Waveform);
                }

                if (a.InlineBytes.HasValue)
                {
                    ap.InlineBytes = ByteString.CopyFrom(a.InlineBytes.Value.Span);
                }

                proto.Attachments.Add(ap);
            }
        }

        return proto;
    }

    private static ChatMessageDto FromChatMessageProto(ChatMessageProto proto)
    {
        PayloadId id = PayloadId.FromBytes(proto.Id.Span);
        ChannelId channelId = ChannelId.FromBytes(proto.ChannelId.Span);
        PublicIdentityId authorId = PublicIdentityId.FromBytes(proto.AuthorId.Span);

        ReplyContextDto? replyContext = proto.ReplyContext != null
            ? new ReplyContextDto(
                PayloadId.FromBytes(proto.ReplyContext.ParentPayloadId.Span),
                PublicIdentityId.FromBytes(proto.ReplyContext.ParentAuthorId.Span))
            : null;

        List<MentionDto> mentions = new(proto.Mentions.Count);
        foreach (MentionProto m in proto.Mentions)
        {
            mentions.Add(new MentionDto(
                PublicIdentityId.FromBytes(m.IdentityId.Span),
                m.StartOffset,
                m.Length));
        }

        LinkPreviewDto? linkPreview = proto.LinkPreview != null
            ? new LinkPreviewDto(
                proto.LinkPreview.Title,
                proto.LinkPreview.Description,
                proto.LinkPreview.CanonicalUrl,
                proto.LinkPreview.ThumbnailBlob != null ? FromBlobRefProto(proto.LinkPreview.ThumbnailBlob) : null)
            : null;

        List<MediaAttachmentDto> attachments = new(proto.Attachments.Count);
        foreach (MediaAttachmentProto a in proto.Attachments)
        {
            attachments.Add(new MediaAttachmentDto(
                new BlobId(a.BlobId),
                a.HasFileName ? a.FileName : null,
                a.ContentType,
                a.ByteSize,
                a.HasWidth ? a.Width : null,
                a.HasHeight ? a.Height : null,
                a.Waveform is { IsEmpty: false } ? a.Waveform.ToByteArray() : null,
                a.InlineBytes is { IsEmpty: false } ? a.InlineBytes.ToByteArray() : null,
                a.OutOfBandReference != null ? FromBlobRefProto(a.OutOfBandReference) : null));
        }

        return new ChatMessageDto(
            id,
            channelId,
            authorId,
            proto.TimestampUnixMs,
            proto.Content,
            mentions,
            replyContext,
            linkPreview,
            attachments,
            proto.HasExpireAfterSeconds ? proto.ExpireAfterSeconds : null,
            proto.HasEditedAtUnixMs ? proto.EditedAtUnixMs : null);
    }

    private static MessageEditProto ToEditProto(MessageEditDto dto)
    {
        MessageEditProto proto = new()
        {
            TargetPayloadId = ByteString.CopyFrom(dto.TargetPayloadId.ToByteArray()),
            ChannelId = ByteString.CopyFrom(dto.ChannelId.ToByteArray()),
            AuthorId = ByteString.CopyFrom(dto.AuthorId.ToByteArray()),
            NewContent = dto.NewContent ?? string.Empty,
            EditedAtUnixMs = dto.EditedAtUnixMs
        };

        if (dto.NewMentions != null)
        {
            foreach (MentionDto m in dto.NewMentions)
            {
                proto.NewMentions.Add(new MentionProto
                {
                    IdentityId = ByteString.CopyFrom(m.IdentityId.ToByteArray()),
                    StartOffset = m.StartOffset,
                    Length = m.Length
                });
            }
        }

        return proto;
    }

    private static MessageEditDto FromEditProto(MessageEditProto proto)
    {
        List<MentionDto> mentions = new(proto.NewMentions.Count);
        foreach (MentionProto m in proto.NewMentions)
        {
            mentions.Add(new MentionDto(
                PublicIdentityId.FromBytes(m.IdentityId.Span),
                m.StartOffset,
                m.Length));
        }

        return new MessageEditDto(
            PayloadId.FromBytes(proto.TargetPayloadId.Span),
            ChannelId.FromBytes(proto.ChannelId.Span),
            PublicIdentityId.FromBytes(proto.AuthorId.Span),
            proto.NewContent,
            mentions,
            proto.EditedAtUnixMs);
    }

    private static MessageTombstoneProto ToTombstoneProto(MessageTombstoneDto dto) =>
        new()
        {
            TargetPayloadId = ByteString.CopyFrom(dto.TargetPayloadId.ToByteArray()),
            ChannelId = ByteString.CopyFrom(dto.ChannelId.ToByteArray()),
            RequestedBy = ByteString.CopyFrom(dto.RequestedBy.ToByteArray()),
            DeletedAtUnixMs = dto.DeletedAtUnixMs
        };

    private static MessageTombstoneDto FromTombstoneProto(MessageTombstoneProto proto) =>
        new(
            PayloadId.FromBytes(proto.TargetPayloadId.Span),
            ChannelId.FromBytes(proto.ChannelId.Span),
            PublicIdentityId.FromBytes(proto.RequestedBy.Span),
            proto.DeletedAtUnixMs);

    private static ReactionUpdateProto ToReactionProto(ReactionUpdateDto dto) =>
        new()
        {
            TargetPayloadId = ByteString.CopyFrom(dto.TargetPayloadId.ToByteArray()),
            ChannelId = ByteString.CopyFrom(dto.ChannelId.ToByteArray()),
            AuthorId = ByteString.CopyFrom(dto.AuthorId.ToByteArray()),
            Emoji = dto.Emoji ?? string.Empty,
            Action = (int)dto.Action,
            TimestampUnixMs = dto.TimestampUnixMs
        };

    private static ReactionUpdateDto FromReactionProto(ReactionUpdateProto proto) =>
        new(
            PayloadId.FromBytes(proto.TargetPayloadId.Span),
            ChannelId.FromBytes(proto.ChannelId.Span),
            PublicIdentityId.FromBytes(proto.AuthorId.Span),
            proto.Emoji,
            (ReactionAction)proto.Action,
            proto.TimestampUnixMs);

    private static ReadReceiptProto ToReadReceiptProto(ReadReceiptDto dto) =>
        new()
        {
            ChannelId = ByteString.CopyFrom(dto.ChannelId.ToByteArray()),
            ReaderId = ByteString.CopyFrom(dto.ReaderId.ToByteArray()),
            UpToPayloadId = ByteString.CopyFrom(dto.UpToPayloadId.ToByteArray()),
            ReadAtUnixMs = dto.ReadAtUnixMs
        };

    private static ReadReceiptDto FromReadReceiptProto(ReadReceiptProto proto) =>
        new(
            ChannelId.FromBytes(proto.ChannelId.Span),
            PublicIdentityId.FromBytes(proto.ReaderId.Span),
            PayloadId.FromBytes(proto.UpToPayloadId.Span),
            proto.ReadAtUnixMs);

    private static SystemNoticeProto ToNoticeProto(SystemNoticeDto dto) =>
        new()
        {
            ChannelId = ByteString.CopyFrom(dto.ChannelId.ToByteArray()),
            NoticeType = (int)dto.NoticeType,
            AffectedIdentity = dto.AffectedIdentity.HasValue
                ? ByteString.CopyFrom(dto.AffectedIdentity.Value.ToByteArray())
                : ByteString.Empty,
            TimestampUnixMs = dto.TimestampUnixMs
        };

    private static SystemNoticeDto FromNoticeProto(SystemNoticeProto proto) =>
        new(
            ChannelId.FromBytes(proto.ChannelId.Span),
            (SystemNoticeType)proto.NoticeType,
            proto.AffectedIdentity is { IsEmpty: false }
                ? PublicIdentityId.FromBytes(proto.AffectedIdentity.Span)
                : null,
            proto.TimestampUnixMs);

    private static GroupInvitationProto ToInviteProto(GroupInvitationDto dto)
    {
        GroupInvitationProto proto = new()
        {
            InvitationId = dto.InvitationId.Value,
            TargetGroupChannelId = ByteString.CopyFrom(dto.TargetGroupChannelId.ToByteArray()),
            GroupName = dto.GroupName ?? string.Empty,
            InviterId = ByteString.CopyFrom(dto.InviterId.ToByteArray()),
            ExpiresAtUnixMs = dto.ExpiresAtUnixMs
        };

        if (dto.RelayEndpoint != null)
        {
            proto.RelayEndpoint = dto.RelayEndpoint;
        }

        return proto;
    }

    private static GroupInvitationDto FromInviteProto(GroupInvitationProto proto) =>
        new(
            new InvitationId(proto.InvitationId),
            ChannelId.FromBytes(proto.TargetGroupChannelId.Span),
            proto.GroupName,
            PublicIdentityId.FromBytes(proto.InviterId.Span),
            proto.HasRelayEndpoint ? proto.RelayEndpoint : null,
            proto.ExpiresAtUnixMs);

    private static GroupInvitationResponseProto ToInviteResponseProto(GroupInvitationResponseDto dto) =>
        new()
        {
            InvitationId = dto.InvitationId.Value,
            TargetGroupChannelId = ByteString.CopyFrom(dto.TargetGroupChannelId.ToByteArray()),
            ResponderId = ByteString.CopyFrom(dto.ResponderId.ToByteArray()),
            Accepted = dto.Accepted,
            RespondedAtUnixMs = dto.RespondedAtUnixMs
        };

    private static GroupInvitationResponseDto FromInviteResponseProto(GroupInvitationResponseProto proto) =>
        new(
            new InvitationId(proto.InvitationId),
            ChannelId.FromBytes(proto.TargetGroupChannelId.Span),
            PublicIdentityId.FromBytes(proto.ResponderId.Span),
            proto.Accepted,
            proto.RespondedAtUnixMs);

    private static ProfileCapabilityProto ToProfileCapabilityProto(ProfileCapabilityDto dto)
    {
        ProfileCapabilityProto proto = new()
        {
            ChannelId = ByteString.CopyFrom(dto.ChannelId.ToByteArray()),
            AuthorId = ByteString.CopyFrom(dto.AuthorId.ToByteArray()),
            ProfileKeyBytes = ByteString.CopyFrom(dto.ProfileKeyBytes),
            BlindedLocatorBytes = ByteString.CopyFrom(dto.BlindedLocatorBytes),
            MailboxRoutingTokenBytes = ByteString.CopyFrom(dto.MailboxRoutingTokenBytes),
            Revision = dto.Revision,
            TimestampUnixMs = dto.TimestampUnixMs,
            SourceType = (int)dto.SourceType
        };

        if (dto.AuthorPublicKeyBytes != null)
        {
            proto.AuthorPublicKeyBytes = ByteString.CopyFrom(dto.AuthorPublicKeyBytes);
        }

        if (dto.SourceEndpoint != null)
        {
            proto.SourceEndpoint = dto.SourceEndpoint;
        }

        return proto;
    }

    private static ProfileCapabilityDto FromProfileCapabilityProto(ProfileCapabilityProto proto) =>
        new(
            ChannelId.FromBytes(proto.ChannelId.Span),
            PublicIdentityId.FromBytes(proto.AuthorId.Span),
            proto.ProfileKeyBytes.ToByteArray(),
            proto.BlindedLocatorBytes.ToByteArray(),
            proto.MailboxRoutingTokenBytes.ToByteArray(),
            proto.Revision,
            proto.TimestampUnixMs,
            proto.AuthorPublicKeyBytes is { IsEmpty: false } ? proto.AuthorPublicKeyBytes.ToByteArray() : null,
            proto.HasSourceEndpoint ? proto.SourceEndpoint : null,
            (ProfileSourceType)proto.SourceType);

    private static BlobReferenceProto ToBlobRefProto(BlobReference b) =>
        new()
        {
            Id = b.Id.HexDigest,
            CiphertextSha256 = ByteString.CopyFrom(b.CiphertextSha256),
            EncryptionKey = ByteString.CopyFrom(b.EncryptionKey),
            BaseNonce = ByteString.CopyFrom(b.BaseNonce),
            PlaintextSizeBytes = b.PlaintextSizeBytes,
            CiphertextSizeBytes = b.CiphertextSizeBytes,
            MimeType = b.MimeType ?? string.Empty,
            ExpiresAtUnixSeconds = b.ExpiresAtUtc.ToUnixTimeSeconds(),
            StorageMode = (uint)b.StorageMode,
            RelayEndpointUri = b.RelayEndpointUri ?? string.Empty,
            BlurHash = b.BlurHash ?? string.Empty,
            InlineThumbnail = b.InlineThumbnail != null ? ByteString.CopyFrom(b.InlineThumbnail) : ByteString.Empty,
            Waveform = b.Waveform != null ? ByteString.CopyFrom(b.Waveform) : ByteString.Empty,
            Width = b.Width ?? 0,
            Height = b.Height ?? 0,
            DurationSeconds = b.DurationSeconds ?? 0
        };

    private static BlobReference FromBlobRefProto(BlobReferenceProto p) =>
        new()
        {
            Id = new BlobId(p.Id),
            CiphertextSha256 = p.CiphertextSha256.ToByteArray(),
            EncryptionKey = p.EncryptionKey.ToByteArray(),
            BaseNonce = p.BaseNonce.ToByteArray(),
            PlaintextSizeBytes = p.PlaintextSizeBytes,
            CiphertextSizeBytes = p.CiphertextSizeBytes,
            MimeType = p.MimeType,
            ExpiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(p.ExpiresAtUnixSeconds),
            StorageMode = (BlobStorageMode)p.StorageMode,
            RelayEndpointUri = string.IsNullOrEmpty(p.RelayEndpointUri) ? null : p.RelayEndpointUri,
            BlurHash = string.IsNullOrEmpty(p.BlurHash) ? null : p.BlurHash,
            InlineThumbnail = p.InlineThumbnail.IsEmpty ? null : p.InlineThumbnail.ToByteArray(),
            Waveform = p.Waveform.IsEmpty ? null : p.Waveform.ToByteArray(),
            Width = p.Width > 0 ? p.Width : null,
            Height = p.Height > 0 ? p.Height : null,
            DurationSeconds = p.DurationSeconds > 0 ? p.DurationSeconds : null
        };
}
