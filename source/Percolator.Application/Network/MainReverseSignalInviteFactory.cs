using System;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Options;
using Percolator.Application.Configuration;
using Percolator.Application.Identity;
using Percolator.Contracts;
using Percolator.Network;

namespace Percolator.Application.Network;

public interface IMainReverseSignalInviteFactory
{
    EstablishDirectSessionRequest CreateInvite();
}

public sealed class MainReverseSignalInviteFactory : IMainReverseSignalInviteFactory
{
    private readonly ActiveIdentityContext _active;
    private readonly ISigningService _signing;
    private readonly IOptions<TransportOptions> _transportOptions;

    public MainReverseSignalInviteFactory(
        ActiveIdentityContext active,
        ISigningService signing,
        IOptions<TransportOptions> transportOptions)
    {
        _active = active;
        _signing = signing;
        _transportOptions = transportOptions;
    }

    public EstablishDirectSessionRequest CreateInvite()
    {
        if (_active.Identity is null)
        {
            throw new InvalidOperationException("Active identity not loaded.");
        }

        var correlation = Guid.NewGuid();
        var port = _transportOptions.Value.GrpcPort;
        if (port == 0) port = 5001;

        var inviterSignedPreKeySpki = _active.Keys?.SignedPreKey?.ExportSubjectPublicKeyInfo();
        if (inviterSignedPreKeySpki is null || inviterSignedPreKeySpki.Length == 0)
        {
            throw new InvalidOperationException("Active identity keys not loaded.");
        }

        var preKeySig = _signing.Sign(new Payload(inviterSignedPreKeySpki));

        var payload = new InviteHandshakeRequestPayload
        {
            Version = 1,
            InviterHost = "localhost",
            InviterPort = (uint)port,
            ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddMinutes(10)),
            RequestCorrelationId = correlation.ToString(),
            InviterPreKey = new InviteHandshakePreKeyBundle
            {
                Version = 1,
                InviterSignedPreKey = ByteString.CopyFrom(inviterSignedPreKeySpki),
                PreKeySignature = ByteString.CopyFrom(preKeySig.Value)
            }
        };

        var payloadBytes = payload.ToByteArray();
        var payloadSig = _signing.Sign(new Payload(payloadBytes));

        var inviterIdentityKeySpki = _signing.GetActivePublicKey().Value;

        return new EstablishDirectSessionRequest
        {
            Version = 1,
            InviterIdentityKey = ByteString.CopyFrom(inviterIdentityKeySpki),
            Payload = ByteString.CopyFrom(payloadBytes),
            PayloadSignature = ByteString.CopyFrom(payloadSig.Value)
        };
    }
}
