using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.Logging.Abstractions;
using Percolator.Application.Network.Handshake;
using Percolator.Contracts;

namespace Percolator.ApplicationTests.Handshake;

[TestFixture]
public sealed class EstablishSessionResponseValidatorTests
{
    [Test]
    public async Task TryValidateAsync_WhenSignatureInvalid_ReturnsNull()
    {
        // Arrange
        var logger = new NullLogger<EstablishSessionResponseValidator>();
        var sut = new EstablishSessionResponseValidator(logger);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();

        var payload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
        {
            Version = 1,
            EphemeralKey = ByteString.CopyFrom(new byte[32]),
            SessionId = new Guid("00000000-0000-0000-0000-000000000009").ToString()
        };
        var payloadBytes = payload.ToByteArray();

        // Sign with a different key than the one in the response
        using var differentEcdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = differentEcdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var response = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(spki),
                ResponsePayload = ByteString.CopyFrom(payloadBytes),
                PayloadSignature = ByteString.CopyFrom(signature)
            }
        };

        // Act
        var result = await sut.TryValidateAsync(response, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task TryValidateAsync_WhenPayloadMissingSessionId_ReturnsNull()
    {
        // Arrange
        var logger = new NullLogger<EstablishSessionResponseValidator>();
        var sut = new EstablishSessionResponseValidator(logger);

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();

        var payload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
        {
            Version = 1,
            EphemeralKey = ByteString.CopyFrom(new byte[32]),
            SessionId = "" // Missing session id
        };
        var payloadBytes = payload.ToByteArray();
        var signature = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var response = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(spki),
                ResponsePayload = ByteString.CopyFrom(payloadBytes),
                PayloadSignature = ByteString.CopyFrom(signature)
            }
        };

        // Act
        var result = await sut.TryValidateAsync(response, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task TryValidateAsync_WhenValid_ReturnsSessionIdAndRemoteHash()
    {
        // Arrange
        var logger = new NullLogger<EstablishSessionResponseValidator>();
        var sut = new EstablishSessionResponseValidator(logger);

        var sessionId = new Guid("00000000-0000-0000-0000-000000000010");

        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var spki = ecdsa.ExportSubjectPublicKeyInfo();

        var payload = new EstablishSessionResponse.Types.Response.Types.ResponsePayload
        {
            Version = 1,
            EphemeralKey = ByteString.CopyFrom(new byte[32]),
            SessionId = sessionId.ToString(),
            PublicIdentityId = ByteString.CopyFrom(new Guid("00000000-0000-0000-0000-000000000011").ToByteArray())
        };
        var payloadBytes = payload.ToByteArray();
        var signature = ecdsa.SignData(payloadBytes, HashAlgorithmName.SHA256);

        var response = new EstablishSessionResponse
        {
            Version = 1,
            Response = new EstablishSessionResponse.Types.Response
            {
                Version = 1,
                IdentitySigningKey = ByteString.CopyFrom(spki),
                ResponsePayload = ByteString.CopyFrom(payloadBytes),
                PayloadSignature = ByteString.CopyFrom(signature)
            }
        };

        // Act
        var result = await sut.TryValidateAsync(response, CancellationToken.None);

        // Assert
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.SessionId.Value, Is.EqualTo(sessionId));
        Assert.That(result.RemoteIdentitySpki, Is.EqualTo(spki));
        Assert.That(result.RemotePublicKeyHash.Length, Is.EqualTo(32));
    }
}
