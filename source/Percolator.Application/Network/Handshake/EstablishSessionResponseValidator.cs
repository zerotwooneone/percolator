using Microsoft.Extensions.Logging;
using Percolator.Contracts;

namespace Percolator.Application.Network.Handshake;

/// <summary>
/// Validates and parses an EstablishSessionResponse to extract session and identity information.
/// </summary>
internal sealed class EstablishSessionResponseValidator : IEstablishSessionResponseValidator
{
    private readonly ILogger<EstablishSessionResponseValidator> _logger;

    public EstablishSessionResponseValidator(ILogger<EstablishSessionResponseValidator> logger)
    {
        _logger = logger;
    }

    public async Task<EstablishSessionResponseValidationResult?> TryValidateAsync(
        EstablishSessionResponse response,
        CancellationToken cancellationToken = default)
    {
        if (response is null)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (response.Response is null
            || !response.Response.HasIdentitySigningKey
            || response.Response.IdentitySigningKey.Length == 0
            || !response.Response.HasResponsePayload
            || response.Response.ResponsePayload.Length == 0
            || !response.Response.HasPayloadSignature
            || response.Response.PayloadSignature.Length == 0)
        {
            return null;
        }

        // Verify responder signature over raw ResponsePayload bytes
        try
        {
            using var ecdsa = System.Security.Cryptography.ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(response.Response.IdentitySigningKey.ToByteArray(), out _);
            if (!ecdsa.VerifyData(
                    response.Response.ResponsePayload.ToByteArray(),
                    response.Response.PayloadSignature.ToByteArray(),
                    System.Security.Cryptography.HashAlgorithmName.SHA256))
            {
                _logger.LogWarning("EstablishSessionResponse signature invalid; dropping");
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to verify EstablishSessionResponse signature; dropping");
            return null;
        }

        var remoteIdentitySpki = response.Response.IdentitySigningKey.ToByteArray();
        var remotePkh = System.Security.Cryptography.SHA256.HashData(remoteIdentitySpki);

        EstablishSessionResponse.Types.Response.Types.ResponsePayload respPayload;
        try
        {
            respPayload = EstablishSessionResponse.Types.Response.Types.ResponsePayload.Parser.ParseFrom(response.Response.ResponsePayload);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Response payload parse failed");
            return null;
        }

        if (!respPayload.HasSessionId || string.IsNullOrWhiteSpace(respPayload.SessionId))
        {
            _logger.LogWarning("Response missing session id");
            return null;
        }

        Percolator.Cryptography.SessionId sid;
        try
        {
            sid = new Percolator.Cryptography.SessionId(Guid.Parse(respPayload.SessionId));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Session id not a GUID: {SessionId}", respPayload.SessionId);
            return null;
        }

        return new EstablishSessionResponseValidationResult(
            sid,
            remoteIdentitySpki,
            remotePkh);
    }
}
