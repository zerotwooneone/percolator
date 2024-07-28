using System.Security.Cryptography;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

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
        var currentTime = _busyService.GetCurrentTime();
        const int arbitraryMinimumKeyLength = 120;
        if (request.Payload.PublicKey.Length < arbitraryMinimumKeyLength)
        {
            _logger.LogWarning("Request with invalid key length: {PublicKeyLength}", request.Payload.PublicKey.Length);
            return Task.FromException<HelloReply>(new Exception("Request with invalid key length: " + request.Payload.PublicKey.Length));
        }

        if (request.PayloadSignature.Length < arbitraryMinimumKeyLength)
        {
            _logger.LogWarning("Request with invalid signature length: {PublicKeySignatureLength}", request.PayloadSignature.Length);
            return Task.FromException<HelloReply>(new Exception("Request with invalid signature length: " + request.PayloadSignature.Length));
        }
        
        //todo: make this configurable
        const double maxHelloTimeoutSeconds = 10;
        if (request.Payload.TimeStampUnixMs <= 0)
        {
            _logger.LogWarning("Request with invalid timestamp: {TimeStampUnixMs}", request.Payload.TimeStampUnixMs);
            return Task.FromException<HelloReply>(new Exception("Request with invalid timestamp: " + request.Payload.TimeStampUnixMs));
        }

        var payloadTimestamp = DateTimeOffset.FromUnixTimeMilliseconds(request.Payload.TimeStampUnixMs);
        var deltaTimespan = payloadTimestamp - currentTime;
        if (Math.Abs(deltaTimespan.TotalSeconds) > maxHelloTimeoutSeconds)
        {
            _logger.LogWarning("Request with invalid timestamp: {TimeStampUnixMs}", request.Payload.TimeStampUnixMs);
            return Task.FromException<HelloReply>(new Exception("Request with invalid timestamp: " + request.Payload.TimeStampUnixMs));
        }

        var otherRsa = new RSACng();
        try
        {
            otherRsa.ImportRSAPublicKey(request.Payload.PublicKey.ToArray(), out _);
        }
        catch (CryptographicException cryptographicException)
        {
            _logger.LogError(cryptographicException, "Failed to import public key");
            return Task.FromException<HelloReply>(cryptographicException);
        }

        //we need the payload bytes to verify the signature
        var payloadBytes = request.Payload.ToByteArray();
        var payloadHash = SHA256.Create().ComputeHash(payloadBytes);
        
        if (!otherRsa.VerifyHash(payloadHash,request.PayloadSignature.ToArray(), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
        {
            return Task.FromException<HelloReply>(new Exception("Failed to verify public key signature"));
        }

        if (_persistenceService.BannedKeys.Contains(request.Payload.PublicKey))
        {
            _logger.LogInformation("Request with banned key: {PublicKey}", request.Payload.PublicKey);
            return Task.FromException<HelloReply>(new Exception("Request with banned key: " + request.Payload.PublicKey));
        }
        var selfPublicKey = _selfEncryptionService.PublicKey;
        var publicKeyHash = SHA256.Create().ComputeHash(selfPublicKey);
        var signedPublicHash = _selfEncryptionService.SignHash(publicKeyHash);
        if (_persistenceService.PendingKeys.TryGetValue(request.Payload.PublicKey, out var pendingExpiration))
        {
            if (pendingExpiration > currentTime)
            {
                _logger.LogInformation("Request while busy key: {PublicKey} will be BANNED", request.Payload.PublicKey);
                _persistenceService.BannedKeys.Add(request.Payload.PublicKey);
                return Task.FromException<HelloReply>(new Exception("Request with banned key: " + request.Payload.PublicKey));
            }
            
            _persistenceService.PendingKeys.TryRemove(request.Payload.PublicKey, out _);
            
            if (!_handshakeService.TryCreateHandshake(otherRsa, out var handshake))
            {
                return Task.FromException<HelloReply>(new Exception("Could not create handshake"));
            }
            return Task.FromResult(new HelloReply
            {
                Proceed = new HelloReply.Types.Proceed
                {
                    PublicKey = ByteString.CopyFrom(selfPublicKey),
                    PublicKeySignature = ByteString.CopyFrom(signedPublicHash),
                    Iv = ByteString.CopyFrom(handshake.Iv),
                    EncryptedSessionKey = ByteString.CopyFrom(handshake.EncryptedSessionKey)
                }
            });
        }
        var notBefore = _busyService.GetDelaySeconds();
        if (notBefore <= 0)
        {
            
            if (!_handshakeService.TryCreateHandshake(otherRsa, out var handshake))
            {
                return Task.FromException<HelloReply>(new Exception("Could not create handshake"));
            }
            return Task.FromResult(new HelloReply
            {
                Proceed = new HelloReply.Types.Proceed
                {
                    PublicKey = ByteString.CopyFrom(selfPublicKey),
                    PublicKeySignature = ByteString.CopyFrom(signedPublicHash),
                    Iv = ByteString.CopyFrom(handshake.Iv),
                    EncryptedSessionKey = ByteString.CopyFrom(handshake.EncryptedSessionKey)
                }
            });
        }

        var dateTimeOffset = currentTime.AddSeconds(notBefore);
        _persistenceService.PendingKeys.AddOrUpdate(request.Payload.PublicKey, dateTimeOffset, (_,_) => dateTimeOffset);
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