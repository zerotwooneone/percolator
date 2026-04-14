using Desktop.Wpf.Features.Sessions.Commands;
using Google.Protobuf;
using MediatR;
using Percolator.Application.Network.Handshake;
using Percolator.Cryptography;
using Percolator.Identity;
using Percolator.Identity.Model;
using System.Security.Cryptography;
using System.Text;
using Percolator.Application.Identity;
using Percolator.Application.Network;
using Percolator.Contracts;

namespace Desktop.Wpf.Features.Sessions.Handlers;

public sealed class DecodeAndQueueInviteCommandHandler : IRequestHandler<DecodeAndQueueInviteCommand, DecodeAndQueueInviteResult>
{
    private readonly IEstablishDirectSessionService _establishDirectSession;
    private readonly ActiveIdentityContext _active;

    public DecodeAndQueueInviteCommandHandler(
        IEstablishDirectSessionService establishDirectSession,
        ActiveIdentityContext active)
    {
        _establishDirectSession = establishDirectSession;
        _active = active;
    }

    public async Task<DecodeAndQueueInviteResult> Handle(DecodeAndQueueInviteCommand request, CancellationToken cancellationToken)
    {
        if (_active.Identity is null)
        {
            return new DecodeAndQueueInviteResult.Failed("Identity not loaded.");
        }

        EstablishDirectSessionRequest env;
        InviteHandshakeRequestPayload payload;
        try
        {
            var bytes = DecodeTokenToBytes(request.Token);
            env = EstablishDirectSessionRequest.Parser.ParseFrom(bytes);
            payload = InviteHandshakeRequestPayload.Parser.ParseFrom(env.Payload);
        }
        catch
        {
            return new DecodeAndQueueInviteResult.Failed("Invalid token.");
        }

        if (!env.HasInviterIdentityKey || env.InviterIdentityKey.Length == 0)
        {
            return new DecodeAndQueueInviteResult.Failed("Token missing inviter identity key.");
        }

        if (!env.HasPayload || env.Payload.Length == 0)
        {
            return new DecodeAndQueueInviteResult.Failed("Token missing payload.");
        }

        if (!env.HasPayloadSignature || env.PayloadSignature.Length == 0)
        {
            return new DecodeAndQueueInviteResult.Failed("Token missing signature.");
        }

        if (!VerifyInvitePayloadSignature(
                inviterIdentityKeySpki: env.InviterIdentityKey.ToByteArray(),
                payloadBytes: env.Payload.ToByteArray(),
                signatureBytes: env.PayloadSignature.ToByteArray()))
        {
            return new DecodeAndQueueInviteResult.Failed("Token signature invalid.");
        }

        try
        {
            _ = await _establishDirectSession.QueueInviteAsync(
                    selfIdentityId: new SelfId(_active.Identity.SelfIdentityId.Value),
                    inviterIdentityKeySpki: env.InviterIdentityKey.ToByteArray(),
                    payloadBytes: env.Payload.ToByteArray(),
                    payloadSignatureBytes: env.PayloadSignature.ToByteArray(),
                    isRelayed: false,
                    relayHostPeerId: null,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new DecodeAndQueueInviteResult.Failed(ex.Message);
        }

        return new DecodeAndQueueInviteResult.Success();
    }

    private static bool VerifyInvitePayloadSignature(byte[] inviterIdentityKeySpki, byte[] payloadBytes, byte[] signatureBytes)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(inviterIdentityKeySpki, out _);
            return ecdsa.VerifyData(payloadBytes, signatureBytes, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] DecodeTokenToBytes(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Token required.");
        }

        var t = new string(token.Where(c => !char.IsWhiteSpace(c)).ToArray());
        return Convert.FromBase64String(t);
    }
}
