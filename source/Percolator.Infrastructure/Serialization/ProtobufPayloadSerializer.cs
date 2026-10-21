using System.Buffers;
using Google.Protobuf;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.Apps.FileTransfer.Features.TransferNegotiation;
using Percolator.Domain.Common;
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
            ManifestId = ByteString.CopyFrom(manifest.ManifestId.Value.ToByteArray(bigEndian: true)),
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
        TransferSessionId manifestId = TransferSessionId.FromGuid(new Guid(proto.ManifestId.Span, bigEndian: true));
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
            TransferSessionId = ByteString.CopyFrom(offer.TransferSessionId.Value.ToByteArray(bigEndian: true)),
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
        TransferSessionId sessionId = TransferSessionId.FromGuid(new Guid(proto.TransferSessionId.Span, bigEndian: true));

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
            TransferSessionId = ByteString.CopyFrom(accept.TransferSessionId.Value.ToByteArray(bigEndian: true)),
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
        TransferSessionId sessionId = TransferSessionId.FromGuid(new Guid(proto.TransferSessionId.Span, bigEndian: true));
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
            TransferSessionId = ByteString.CopyFrom(cancel.TransferSessionId.Value.ToByteArray(bigEndian: true)),
            Reason = cancel.Reason.Value
        };

    private static FileTransferCancelPayload FromCancelProto(FileTransferCancelProto proto) =>
        new(
            TransferSessionId.FromGuid(new Guid(proto.TransferSessionId.Span, bigEndian: true)),
            TransferTerminationReason.FromText(proto.Reason));
}
