using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Percolator.Protobuf;
using StreamMessage = Percolator.Protobuf.StreamMessage;

namespace Percolator.Grpc.Services;

public class GreeterService : Greeter.GreeterBase
{
    private readonly ILogger<GreeterService> _logger;
    private readonly PersistenceService _persistenceService;
    private readonly BusyService _busyService;
    private readonly HandshakeService _handshakeService;
    private readonly SelfEncryptionService _selfEncryptionService;

    public GreeterService(
        ILogger<GreeterService> logger,
        PersistenceService persistenceService,
        BusyService busyService,
        HandshakeService handshakeService, 
        SelfEncryptionService selfEncryptionService)
    {
        _logger = logger;
        _persistenceService = persistenceService;
        _busyService = busyService;
        _handshakeService = handshakeService;
        _selfEncryptionService = selfEncryptionService;
    }

    public override Task<HelloReply> SayHello(HelloRequest request, ServerCallContext context)
    {
        var currentUtcTime = _busyService.GetCurrentUtcTime();
        const int arbitraryMinimumKeyLength = 120;
        if (request.Payload.IdentityKey == null)
        {
            _logger.LogWarning("Request with missing identity key");
            return Task.FromException<HelloReply>(new Exception("Request with missing identity key"));
        }

        if (request.Payload.IdentityKey.Length < arbitraryMinimumKeyLength)
        {
            _logger.LogWarning("Request with invalid key length: {PublicKeyLength}", request.Payload.IdentityKey.Length);
            return Task.FromException<HelloReply>(new Exception("Request with invalid key length: " + request.Payload.IdentityKey.Length));
        }

        if (request.Payload.EphemeralKey == null)
        {
            _logger.LogWarning("Request with missing ephemeral key");
            return Task.FromException<HelloReply>(new Exception("Request with missing ephemeral key"));
        }

        if (request.Payload.EphemeralKey.Length < arbitraryMinimumKeyLength)
        {
            _logger.LogWarning("Request with invalid ephemeral key length: {PublicKeyLength}", request.Payload.EphemeralKey.Length);
            return Task.FromException<HelloReply>(new Exception("Request with invalid ephemeral key length: " + request.Payload.EphemeralKey.Length));
        }

        if (request.PayloadSignature.Length < arbitraryMinimumKeyLength)
        {
            _logger.LogWarning("Request with invalid signature length: {PublicKeySignatureLength}", request.PayloadSignature.Length);
            return Task.FromException<HelloReply>(new Exception("Request with invalid signature length: " + request.PayloadSignature.Length));
        }
        
        //todo: make this configurable
        const double maxHelloTimeoutSeconds = 10;
        if (request.Payload.TimeStampUnixUtcMs <= 0)
        {
            _logger.LogWarning("Request with invalid timestamp: {TimeStampUnixMs}", request.Payload.TimeStampUnixUtcMs);
            return Task.FromException<HelloReply>(new Exception("Request with invalid timestamp: " + request.Payload.TimeStampUnixUtcMs));
        }

        var payloadTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(request.Payload.TimeStampUnixUtcMs);
        var deltaTimespan = payloadTimestamp - currentUtcTime;
        if (Math.Abs(deltaTimespan.TotalSeconds) > maxHelloTimeoutSeconds)
        {
            _logger.LogWarning("Request with invalid timestamp: {TimeStampUnixMs}", request.Payload.TimeStampUnixUtcMs);
            return Task.FromException<HelloReply>(new Exception("Request with invalid timestamp: " + request.Payload.TimeStampUnixUtcMs));
        }

        var callerIdentity = new RSACng();
        try
        {
            callerIdentity.ImportRSAPublicKey(request.Payload.IdentityKey.ToArray(), out _);
        }
        catch (CryptographicException cryptographicException)
        {
            _logger.LogError(cryptographicException, "Failed to import caller identity key");
            return Task.FromException<HelloReply>(cryptographicException);
        }
        
        var callerEphemeral = new RSACng();
        try
        {
            callerEphemeral.ImportRSAPublicKey(request.Payload.EphemeralKey.ToArray(), out _);
        }
        catch (CryptographicException cryptographicException)
        {
            _logger.LogError(cryptographicException, "Failed to import caller ephemeral key");
            return Task.FromException<HelloReply>(cryptographicException);
        }

        //we need the payload bytes to verify the signature
        var requestPayloadBytes = request.Payload.ToByteArray();
        
        if (!callerIdentity.VerifyData(requestPayloadBytes,request.PayloadSignature.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return Task.FromException<HelloReply>(new Exception("Failed to verify public key signature"));
        }

        if (_persistenceService.BannedKeys.Contains(request.Payload.IdentityKey))
        {
            _logger.LogInformation("Request with banned key: {PublicKey}", request.Payload.IdentityKey);
            return Task.FromException<HelloReply>(new Exception("Request with banned key: " + request.Payload.IdentityKey));
        }
        var selfIdentityKey = _selfEncryptionService.Identity.ExportRSAPublicKey();
        var selfEphemeralKey = _selfEncryptionService.Ephemeral.ExportRSAPublicKey();
        if (_persistenceService.PendingKeys.TryGetValue(request.Payload.IdentityKey, out var pendingExpiration))
        {
            if (pendingExpiration > currentUtcTime)
            {
                _logger.LogInformation("Request while busy key: {PublicKey} will be BANNED", request.Payload.IdentityKey);
                _persistenceService.BannedKeys.Add(request.Payload.IdentityKey);
                return Task.FromException<HelloReply>(new Exception("Request with banned key: " + request.Payload.IdentityKey));
            }
            
            _persistenceService.PendingKeys.TryRemove(request.Payload.IdentityKey, out _);
            
            if (!_handshakeService.TryCreateHandshake(callerEphemeral, out var handshake))
            {
                return Task.FromException<HelloReply>(new Exception("Could not create handshake"));
            }

            var payload = new HelloReply.Types.Proceed.Types.Payload
            {
                IdentityKey = ByteString.CopyFrom(selfIdentityKey),
                EphemeralKey = ByteString.CopyFrom(selfEphemeralKey),
                Iv = ByteString.CopyFrom(handshake.Key.IV),
                EncryptedSessionKey = ByteString.CopyFrom(handshake.EncryptedSessionKey),
                TimeStampUnixUtcMs = currentUtcTime.ToUnixTimeMilliseconds()
            };
            var responsePayloadBytes= payload.ToByteArray();
            var responsePayloadHashSignature = _selfEncryptionService.Identity.SignData(responsePayloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _persistenceService.KnownSessionsByIdentity.AddOrUpdate(request.Payload.IdentityKey,
                _=> new SessionTracker(new KnownSession
                {
                    SessionKey = handshake.Key,
                    EphemeralPublicKey = callerEphemeral
                }), 
                (_,old) =>
                {
                    old.ReplaceCurrent(new KnownSession
                    {
                        SessionKey = handshake.Key,
                        EphemeralPublicKey = callerEphemeral
                    });
                    return old;
                });
            return Task.FromResult(new HelloReply
            {
                Proceed = new HelloReply.Types.Proceed
                {
                    Payload = payload,
                    PayloadSignature = ByteString.CopyFrom(responsePayloadHashSignature),
                }
            });
        }
        var notBefore = _busyService.GetDelaySeconds();
        if (notBefore <= 0)
        {
            if (!_handshakeService.TryCreateHandshake(callerEphemeral, out var handshake))
            {
                return Task.FromException<HelloReply>(new Exception("Could not create handshake"));
            }
            var payload = new HelloReply.Types.Proceed.Types.Payload
            {
                IdentityKey = ByteString.CopyFrom(selfIdentityKey),
                EphemeralKey = ByteString.CopyFrom(selfEphemeralKey),
                Iv = ByteString.CopyFrom(handshake.Key.IV),
                EncryptedSessionKey = ByteString.CopyFrom(handshake.EncryptedSessionKey),
                TimeStampUnixUtcMs = currentUtcTime.ToUnixTimeMilliseconds()
            };
            var responsePayloadBytes= payload.ToByteArray();
            var responsePayloadHashSignature = _selfEncryptionService.Identity.SignData(responsePayloadBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            _persistenceService.KnownSessionsByIdentity.AddOrUpdate(request.Payload.IdentityKey,
                _=> new SessionTracker(new KnownSession
                {
                    SessionKey = handshake.Key,
                    EphemeralPublicKey = callerEphemeral
                }), 
                (_,old) =>
                {
                    old.ReplaceCurrent(new KnownSession
                    {
                        SessionKey = handshake.Key,
                        EphemeralPublicKey = callerEphemeral
                    });
                    return old;
                });
            return Task.FromResult(new HelloReply
            {
                Proceed = new HelloReply.Types.Proceed
                {
                    Payload = payload,
                    PayloadSignature = ByteString.CopyFrom(responsePayloadHashSignature),
                }
            });
        }

        var dateTimeOffset = currentUtcTime.AddSeconds(notBefore);
        _persistenceService.PendingKeys.AddOrUpdate(request.Payload.IdentityKey, dateTimeOffset, (_,_) => dateTimeOffset);
        return Task.FromResult(new HelloReply
        {
            Busy = new HelloReply.Types.Busy
            {
                NotBeforeUnixMs = dateTimeOffset.ToUnixTimeMilliseconds()
            }
        });
    }

    public override Task Gimme(Empty request, IServerStreamWriter<StreamMessage> responseStream, ServerCallContext context)
    {
        using var readStream = File.OpenRead("gradient.bmp");
        const int bufferSize = 1024;
        var buffer = new byte[bufferSize];
        int startIndex = 0;
        while (readStream.Read(buffer, 0, buffer.Length) > 0)
        {
            var streamMessage = new StreamMessage
            {
                StartIndex = startIndex
            };
            streamMessage.Bytes.Add(ByteString.CopyFrom(buffer));
            responseStream.WriteAsync(streamMessage);
            startIndex += bufferSize;
        }
        return Task.CompletedTask;
    }
}