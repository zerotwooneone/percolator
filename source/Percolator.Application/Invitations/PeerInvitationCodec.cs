namespace Percolator.Application.Invitations;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Domain.Identities;
using Percolator.Domain.Identities.ValueObjects;

public sealed class PeerInvitationCodec : IPeerInvitationCodec
{
    private const string SchemePrefix = "percolator://";
    private const byte BinaryVersion = 1;

    public string Encode(PeerInvitationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        var sb = new StringBuilder();
        sb.Append(SchemePrefix).Append("invite?");
        sb.Append("id=").Append(payload.IdentityId.Value.ToString("D"));
        sb.Append("&route=").Append(payload.RoutingToken.Value.ToString("N"));
        sb.Append("&deliv=").Append(Convert.ToHexStringLower(payload.DeliveryToken.Span));

        if (payload.EndpointHosts != null && payload.EndpointHosts.Count > 0)
        {
            sb.Append("&endpoints=").Append(Uri.EscapeDataString(string.Join(",", payload.EndpointHosts)));
        }

        if (payload.PortRange != null)
        {
            sb.Append("&ports=").Append(payload.PortRange.IsSinglePort
                ? payload.PortRange.MinPort.ToString()
                : $"{payload.PortRange.MinPort}-{payload.PortRange.MaxPort}");
        }

        return sb.ToString();
    }

    public string EncodeAsBase64(PeerInvitationPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        using var ms = new MemoryStream();
        using var writer = new BinaryWriter(ms, Encoding.UTF8);

        writer.Write(BinaryVersion);
        writer.Write(payload.IdentityId.ToByteArray());
        writer.Write(payload.RoutingToken.ToByteArray());
        writer.Write(payload.DeliveryToken.Span);

        if (payload.PortRange != null)
        {
            writer.Write((byte)1);
            writer.Write((ushort)payload.PortRange.MinPort);
            writer.Write((ushort)payload.PortRange.MaxPort);
        }
        else
        {
            writer.Write((byte)0);
        }

        var hosts = payload.EndpointHosts ?? Array.Empty<string>();
        writer.Write((ushort)hosts.Count);
        foreach (var host in hosts)
        {
            writer.Write(host);
        }

        return Convert.ToBase64String(ms.ToArray());
    }

    public DomainResult<PeerInvitationPayload> Decode(string invitationString)
    {
        if (string.IsNullOrWhiteSpace(invitationString))
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_INVITATION", "Invitation string cannot be empty."));
        }

        var input = invitationString.Trim();
        if (input.StartsWith(SchemePrefix, StringComparison.OrdinalIgnoreCase))
        {
            input = input.Substring(SchemePrefix.Length);
        }

        if (input.Contains('?') || input.Contains("id=", StringComparison.OrdinalIgnoreCase))
        {
            return DecodeFromUrl(input);
        }

        return DecodeFromBase64(input);
    }

    private static DomainResult<PeerInvitationPayload> DecodeFromUrl(string input)
    {
        var cleanInput = input;
        var hashIndex = cleanInput.IndexOf('#');
        if (hashIndex >= 0)
        {
            cleanInput = cleanInput.Substring(0, hashIndex);
        }

        var queryString = cleanInput;
        var queryIndex = cleanInput.IndexOf('?');
        if (queryIndex >= 0)
        {
            queryString = cleanInput.Substring(queryIndex + 1);
        }

        var pairs = queryString.Split('&', StringSplitOptions.RemoveEmptyEntries);
        var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            var eqIndex = pair.IndexOf('=');
            if (eqIndex > 0)
            {
                var key = Uri.UnescapeDataString(pair.Substring(0, eqIndex)).Trim();
                var val = Uri.UnescapeDataString(pair.Substring(eqIndex + 1)).Trim();
                queryParams[key] = val;
            }
        }

        if (!queryParams.TryGetValue("id", out var idStr) || !Guid.TryParse(idStr, out var idGuid) || idGuid == Guid.Empty)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_IDENTITY_ID", "Invalid or missing identity ID in invitation."));
        }

        if (!queryParams.TryGetValue("route", out var routeStr) || !Guid.TryParse(routeStr, out var routeGuid) || routeGuid == Guid.Empty)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_ROUTING_TOKEN", "Invalid or missing routing token in invitation."));
        }

        if (!queryParams.TryGetValue("deliv", out var delivStr) || string.IsNullOrWhiteSpace(delivStr))
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_DELIVERY_TOKEN", "Missing delivery token in invitation."));
        }

        byte[] delivBytes;
        try
        {
            delivBytes = Convert.FromHexString(delivStr);
            if (delivBytes.Length != 32)
            {
                return DomainResult<PeerInvitationPayload>.Failure(
                    new DomainError("INVALID_DELIVERY_TOKEN", "Delivery token must be exactly 32 bytes (64 hex characters)."));
            }
        }
        catch (FormatException)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_DELIVERY_TOKEN", "Delivery token contains invalid hex characters."));
        }

        var endpointHosts = new List<string>();
        if (queryParams.TryGetValue("endpoints", out var endpointsStr) && !string.IsNullOrWhiteSpace(endpointsStr))
        {
            endpointHosts.AddRange(endpointsStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        PortRange? portRange = PortRange.DefaultDynamicRange;
        if (queryParams.TryGetValue("ports", out var portsStr) && !string.IsNullOrWhiteSpace(portsStr))
        {
            var dashIndex = portsStr.IndexOf('-');
            if (dashIndex > 0)
            {
                var minStr = portsStr.Substring(0, dashIndex);
                var maxStr = portsStr.Substring(dashIndex + 1);
                if (int.TryParse(minStr, out var minPort) && int.TryParse(maxStr, out var maxPort) &&
                    minPort >= 1 && maxPort <= 65535 && minPort <= maxPort)
                {
                    portRange = new PortRange(minPort, maxPort);
                }
                else
                {
                    return DomainResult<PeerInvitationPayload>.Failure(
                        new DomainError("INVALID_PORT_RANGE", "Invalid port range specified in invitation."));
                }
            }
            else if (int.TryParse(portsStr, out var singlePort) && singlePort >= 1 && singlePort <= 65535)
            {
                portRange = PortRange.Single(singlePort);
            }
            else
            {
                return DomainResult<PeerInvitationPayload>.Failure(
                    new DomainError("INVALID_PORT_RANGE", "Invalid port range specified in invitation."));
            }
        }

        var identityId = PublicIdentityId.FromGuid(idGuid);
        var routingToken = BlindedRoutingToken.FromGuid(routeGuid);
        var deliveryToken = DeliveryToken.FromSpan(delivBytes);

        return DomainResult<PeerInvitationPayload>.Success(
            new PeerInvitationPayload(identityId, routingToken, deliveryToken, endpointHosts, portRange));
    }

    private static DomainResult<PeerInvitationPayload> DecodeFromBase64(string base64String)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64String);
        }
        catch (FormatException)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("UNSUPPORTED_INVITATION_FORMAT", "Invitation string is neither a valid URL nor a valid Base64 payload."));
        }

        if (bytes.Length < 1 + 16 + 16 + 32 + 1 + 2)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("INVALID_INVITATION_PAYLOAD", "Binary invitation payload is too short."));
        }

        if (bytes.Length > 4096)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("PAYLOAD_TOO_LARGE", "Binary invitation payload exceeds maximum allowed size (4096 bytes)."));
        }

        try
        {
            using var ms = new MemoryStream(bytes);
            using var reader = new BinaryReader(ms, Encoding.UTF8);

            var version = reader.ReadByte();
            if (version != BinaryVersion)
            {
                return DomainResult<PeerInvitationPayload>.Failure(
                    new DomainError("UNSUPPORTED_VERSION", $"Unsupported binary invitation version: {version}."));
            }

            var idBytes = reader.ReadBytes(16);
            var routeBytes = reader.ReadBytes(16);
            var delivBytes = reader.ReadBytes(32);

            var identityId = PublicIdentityId.FromBytes(idBytes);
            var routingToken = BlindedRoutingToken.FromBytes(routeBytes);
            var deliveryToken = DeliveryToken.FromSpan(delivBytes);

            PortRange? portRange = null;
            var hasPortRange = reader.ReadByte();
            if (hasPortRange == 1)
            {
                var min = reader.ReadUInt16();
                var max = reader.ReadUInt16();
                portRange = new PortRange(min, max);
            }
            else
            {
                portRange = PortRange.DefaultDynamicRange;
            }

            var hostCount = reader.ReadUInt16();
            if (hostCount > 64)
            {
                return DomainResult<PeerInvitationPayload>.Failure(
                    new DomainError("INVALID_INVITATION_PAYLOAD", "Binary invitation payload contains too many endpoint hosts (max 64)."));
            }

            var endpointHosts = new List<string>(hostCount);
            for (var i = 0; i < hostCount; i++)
            {
                endpointHosts.Add(reader.ReadString());
            }

            return DomainResult<PeerInvitationPayload>.Success(
                new PeerInvitationPayload(identityId, routingToken, deliveryToken, endpointHosts, portRange));
        }
        catch (Exception ex)
        {
            return DomainResult<PeerInvitationPayload>.Failure(
                new DomainError("CORRUPT_PAYLOAD", $"Binary invitation payload deserialization failed: {ex.Message}"));
        }
    }
}
