using System.Text.Json;
using Percolator.Apps.FileTransfer.Domain.Common;
using Percolator.Apps.FileTransfer.Domain.ManifestManagement;
using Percolator.Apps.FileTransfer.Domain.TransferNegotiation;
using Percolator.PluginSdk;

namespace Percolator.Apps.FileTransfer.Features.TransferNegotiation;

public static class FileTransferPayloadSerializer
{
    public static byte[] SerializeOffer(FileTransferOfferPayload offer)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", offer.TransferSessionId.ToString());

            // Summary
            writer.WriteStartObject("summary");
            writer.WriteString("rootName", offer.Summary.RootName.Value);
            writer.WriteNumber("totalSize", offer.Summary.TotalSize.Bytes);
            writer.WriteNumber("totalFiles", offer.Summary.TotalFiles.Count);
            writer.WriteNumber("chunkSize", offer.Summary.ChunkSizeBytes.Bytes);
            writer.WriteNumber("totalChunks", offer.Summary.TotalChunks.Count);
            writer.WriteString("merkleRoot", Convert.ToBase64String(offer.Summary.MerkleRoot.Span));
            writer.WriteEndObject();

            // Manifest Distribution
            writer.WriteStartObject("manifest");
            writer.WriteNumber("kind", (byte)offer.Manifest.Kind);
            if (offer.Manifest.Kind == ManifestDistributionKind.Inline && offer.Manifest.InlineData != null)
            {
                writer.WriteString("inlineData", Convert.ToBase64String(offer.Manifest.InlineData.Span));
            }
            else if (offer.Manifest.Kind == ManifestDistributionKind.RemoteBlob && offer.Manifest.BlobDescriptor != null)
            {
                writer.WriteStartObject("blob");
                writer.WriteString("blobId", offer.Manifest.BlobDescriptor.BlobId.HexDigest);
                writer.WriteString("sha256", Convert.ToBase64String(offer.Manifest.BlobDescriptor.CiphertextSha256.Span));
                writer.WriteNumber("plaintextSize", offer.Manifest.BlobDescriptor.PlaintextSizeBytes.Bytes);
                writer.WriteString("relayUri", offer.Manifest.BlobDescriptor.RelayEndpointUri.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndObject();

            // Crypto secrets
            writer.WriteString("sessionKey", Convert.ToBase64String(offer.SessionEncryptionKey.Span));
            writer.WriteString("baseNonce", Convert.ToBase64String(offer.BaseNonce.Span));
            writer.WriteString("streamAuthToken", Convert.ToBase64String(offer.StreamAuthToken.Span));

            // Relay rendezvous
            if (offer.RelayRendezvous != null)
            {
                writer.WriteStartObject("relay");
                writer.WriteString("endpointUri", offer.RelayRendezvous.EndpointUri.Value);
                writer.WriteString("token", Convert.ToBase64String(offer.RelayRendezvous.Token.Span));
                writer.WriteEndObject();
            }

            // Direct candidates
            writer.WriteStartArray("candidates");
            foreach (DirectEndpointCandidate candidate in offer.DirectCandidates)
            {
                writer.WriteStartObject();
                writer.WriteString("host", candidate.Host.Value);
                writer.WriteNumber("port", candidate.Port.Value);
                writer.WriteNumber("kind", (byte)candidate.Kind);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return ms.ToArray();
    }

    public static FileTransferOfferPayload DeserializeOffer(ReadOnlySpan<byte> span)
    {
        using JsonDocument doc = JsonDocument.Parse(span.ToArray());
        JsonElement root = doc.RootElement;

        TransferSessionId sessionId = TransferSessionId.FromGuid(Guid.Parse(root.GetProperty("sessionId").GetString()!));

        JsonElement summaryEl = root.GetProperty("summary");
        RootBundleName rootName = RootBundleName.Parse(summaryEl.GetProperty("rootName").GetString()!);
        TotalBundleBytes totalSize = TotalBundleBytes.FromBytes(summaryEl.GetProperty("totalSize").GetInt64());
        FileCount totalFiles = FileCount.FromInt(summaryEl.GetProperty("totalFiles").GetInt32());
        ChunkSizeBytes chunkSize = ChunkSizeBytes.FromBytes(summaryEl.GetProperty("chunkSize").GetInt32());
        ChunkCount totalChunks = ChunkCount.FromInt(summaryEl.GetProperty("totalChunks").GetInt32());
        MerkleRoot merkleRoot = MerkleRoot.FromBytes(Convert.FromBase64String(summaryEl.GetProperty("merkleRoot").GetString()!));

        TransferBundleSummary summary = TransferBundleSummary.Create(rootName, totalSize, totalFiles, chunkSize, totalChunks, merkleRoot);

        JsonElement manifestEl = root.GetProperty("manifest");
        ManifestDistributionKind manifestKind = (ManifestDistributionKind)manifestEl.GetProperty("kind").GetByte();
        ManifestDistribution manifestDist;
        if (manifestKind == ManifestDistributionKind.Inline)
        {
            byte[] inlineBytes = Convert.FromBase64String(manifestEl.GetProperty("inlineData").GetString()!);
            manifestDist = ManifestDistribution.FromInline(InlineManifestData.FromBytes(inlineBytes));
        }
        else
        {
            JsonElement blobEl = manifestEl.GetProperty("blob");
            BlobId blobId = new BlobId(blobEl.GetProperty("blobId").GetString()!);
            ChunkHash sha256 = ChunkHash.FromBytes(Convert.FromBase64String(blobEl.GetProperty("sha256").GetString()!));
            TotalBundleBytes plaintextSize = TotalBundleBytes.FromBytes(blobEl.GetProperty("plaintextSize").GetInt64());
            RelayPipeEndpointUri relayUri = RelayPipeEndpointUri.Parse(blobEl.GetProperty("relayUri").GetString()!);

            manifestDist = ManifestDistribution.FromBlob(new ManifestBlobDescriptor(blobId, sha256, plaintextSize, relayUri));
        }

        TransferKey sessionKey = TransferKey.FromBytes(Convert.FromBase64String(root.GetProperty("sessionKey").GetString()!));
        BaseNonce baseNonce = BaseNonce.FromBytes(Convert.FromBase64String(root.GetProperty("baseNonce").GetString()!));
        StreamAuthToken streamAuthToken = StreamAuthToken.FromBytes(Convert.FromBase64String(root.GetProperty("streamAuthToken").GetString()!));

        RelayRendezvousDescriptor? relay = null;
        if (root.TryGetProperty("relay", out JsonElement relayEl) && relayEl.ValueKind == JsonValueKind.Object)
        {
            RelayPipeEndpointUri endpointUri = RelayPipeEndpointUri.Parse(relayEl.GetProperty("endpointUri").GetString()!);
            RelayPipeToken token = RelayPipeToken.FromBytes(Convert.FromBase64String(relayEl.GetProperty("token").GetString()!));
            relay = new RelayRendezvousDescriptor(endpointUri, token);
        }

        List<DirectEndpointCandidate> candidates = [];
        if (root.TryGetProperty("candidates", out JsonElement candidatesEl) && candidatesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in candidatesEl.EnumerateArray())
            {
                EndpointHost host = EndpointHost.Parse(c.GetProperty("host").GetString()!);
                PortNumber port = PortNumber.FromInt(c.GetProperty("port").GetInt32());
                DirectCandidateKind kind = (DirectCandidateKind)c.GetProperty("kind").GetByte();
                candidates.Add(new DirectEndpointCandidate(host, port, kind));
            }
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

    public static byte[] SerializeAccept(FileTransferAcceptPayload accept)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", accept.TransferSessionId.ToString());
            writer.WriteNumber("routingPreference", (byte)accept.RoutingPreference);

            writer.WriteStartArray("candidates");
            foreach (DirectEndpointCandidate candidate in accept.ReceiverCandidates)
            {
                writer.WriteStartObject();
                writer.WriteString("host", candidate.Host.Value);
                writer.WriteNumber("port", candidate.Port.Value);
                writer.WriteNumber("kind", (byte)candidate.Kind);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteNumber("totalChunks", accept.ResumptionBitfield.TotalChunks.Count);
            writer.WriteString("bitfield", Convert.ToBase64String(accept.ResumptionBitfield.ToByteArray()));

            writer.WriteEndObject();
        }

        return ms.ToArray();
    }

    public static FileTransferAcceptPayload DeserializeAccept(ReadOnlySpan<byte> span)
    {
        using JsonDocument doc = JsonDocument.Parse(span.ToArray());
        JsonElement root = doc.RootElement;

        TransferSessionId sessionId = TransferSessionId.FromGuid(Guid.Parse(root.GetProperty("sessionId").GetString()!));
        TransportRoutingPreference routing = (TransportRoutingPreference)root.GetProperty("routingPreference").GetByte();

        List<DirectEndpointCandidate> candidates = [];
        if (root.TryGetProperty("candidates", out JsonElement candidatesEl) && candidatesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement c in candidatesEl.EnumerateArray())
            {
                EndpointHost host = EndpointHost.Parse(c.GetProperty("host").GetString()!);
                PortNumber port = PortNumber.FromInt(c.GetProperty("port").GetInt32());
                DirectCandidateKind kind = (DirectCandidateKind)c.GetProperty("kind").GetByte();
                candidates.Add(new DirectEndpointCandidate(host, port, kind));
            }
        }

        int totalChunks = root.GetProperty("totalChunks").GetInt32();
        byte[] bitfieldBytes = Convert.FromBase64String(root.GetProperty("bitfield").GetString()!);
        Bitfield bitfield = Bitfield.FromBytes(bitfieldBytes, ChunkCount.FromInt(totalChunks));

        return new FileTransferAcceptPayload(sessionId, candidates, routing, bitfield);
    }

    public static byte[] SerializeCancel(FileTransferCancelPayload cancel)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            writer.WriteStartObject();
            writer.WriteString("sessionId", cancel.TransferSessionId.ToString());
            writer.WriteString("reason", cancel.Reason.Value);
            writer.WriteEndObject();
        }

        return ms.ToArray();
    }

    public static FileTransferCancelPayload DeserializeCancel(ReadOnlySpan<byte> span)
    {
        using JsonDocument doc = JsonDocument.Parse(span.ToArray());
        JsonElement root = doc.RootElement;

        TransferSessionId sessionId = TransferSessionId.FromGuid(Guid.Parse(root.GetProperty("sessionId").GetString()!));
        string reasonStr = root.GetProperty("reason").GetString()!;
        TransferTerminationReason reason = TransferTerminationReason.FromText(reasonStr);

        return new FileTransferCancelPayload(sessionId, reason);
    }
}
