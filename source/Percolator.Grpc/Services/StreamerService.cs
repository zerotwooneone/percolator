using Google.Protobuf;
using Grpc.Core;
using Percolator.Protobuf.Stream;

namespace Percolator.Grpc.Services;

public class StreamerService : Streamer.StreamerBase
{
    private readonly ILogger<StreamerService> _logger;
    private readonly PersistenceService _persistenceService;
    private readonly BusyService _busyService;
    private readonly HandshakeService _handshakeService;
    private readonly SelfEncryptionService _selfEncryptionService;

    public StreamerService(
        ILogger<StreamerService> logger,
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

    public override async Task Begin(IAsyncStreamReader<StreamMessage> requestStream,
        IServerStreamWriter<StreamMessage> responseStream, ServerCallContext context)
    {
        //todo: add a message handling queue on a single thread
        await Task.Factory.StartNew( () =>
        {
            try
            {
                while (requestStream.MoveNext().Result)
                {
                    var currentUtcTime = _busyService.GetCurrentUtcTime();
                    var message = requestStream.Current;

                    if (message.Identity == null)
                    {
                        _logger.LogWarning("Request with missing identity key");
                        continue;
                    }

                    const int arbitraryMinimumKeyLength = 120;
                    if (message.Identity.Length < arbitraryMinimumKeyLength)
                    {
                        _logger.LogWarning("Request with invalid key length: {PublicKeyLength}", message.Identity.Length);
                        continue;
                    }

                    if (!_persistenceService.KnownSessionsByIdentity.TryGetValue(
                            message.Identity,
                            out var session))
                    {
                        _logger.LogWarning("Request with unknown identity");
                        continue;
                    }

                    if (message.EncryptedPayload == null || message.EncryptedPayload.Length == 0)
                    {
                        _logger.LogWarning("Request with missing encrypted payload");
                        continue;
                    }

                    var encryptedBytes = message.EncryptedPayload.ToByteArray();
                    var payloadBytes = session.Current.SessionKey.NaiveDecrypt(encryptedBytes).Result;
                    var payload = StreamMessage.Types.Payload.Parser.ParseFrom(payloadBytes);
                    if (payload.PayloadTypeCase == StreamMessage.Types.Payload.PayloadTypeOneofCase.None)
                    {
                        _logger.LogWarning("Request with missing payload type");
                        continue;
                    }

                    switch (payload.PayloadTypeCase)
                    {
                        case StreamMessage.Types.Payload.PayloadTypeOneofCase.UserChat:
                        {
                            var userMessage = payload.UserChat;
                            var chatResponse = new StreamMessage.Types.Payload
                            {
                                UserChat = new StreamMessage.Types.Payload.Types.UserMessage
                                {
                                    TimeStampUnixUtcMs = currentUtcTime.ToUnixTimeMilliseconds(),
                                    Message = $"responding to : {userMessage.Message}"
                                }
                            };
                            var chatBytes = chatResponse.ToByteArray();
                            var encryptedPayload = session.Current.SessionKey.NaiveEncrypt(chatBytes).Result;

                            responseStream.WriteAsync(new StreamMessage
                            {
                                Identity = ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey()),
                                EncryptedPayload = ByteString.CopyFrom(encryptedPayload)
                            }).Wait();
                            break;
                        }
                        case StreamMessage.Types.Payload.PayloadTypeOneofCase.Ping:
                            var pingMessage = payload.Ping;
                            var pingResponse = new StreamMessage.Types.Payload
                            {
                                Pong = new StreamMessage.Types.Payload.Types.PongMessage
                                {
                                    TimeStampUnixUtcMs = currentUtcTime.ToUnixTimeMilliseconds(),
                                    Delta = currentUtcTime.ToUnixTimeMilliseconds() - pingMessage.TimeStampUnixUtcMs
                                }
                            };
                            var pongBytes = pingResponse.ToByteArray();
                            var encryptedPong = session.Current.SessionKey.NaiveEncrypt(pongBytes).Result;
                            responseStream.WriteAsync(new StreamMessage
                            {
                                Identity = ByteString.CopyFrom(_selfEncryptionService.Identity.ExportRSAPublicKey()),
                                EncryptedPayload = ByteString.CopyFrom(encryptedPong)
                            }).Wait();
                            break;
                        case StreamMessage.Types.Payload.PayloadTypeOneofCase.Heartbeat:
                        case StreamMessage.Types.Payload.PayloadTypeOneofCase.Pong:
                            continue;
                            break;
                        default:
                            _logger.LogWarning("Unknown payload type: {PayloadType}", (int) payload.PayloadTypeCase);
                            break;
                    }
                }
            }
            catch (IOException ioException) when(ioException.Message.StartsWith("The client reset the request stream"))
            {
                _logger.LogInformation("Client disconnected. ip: {Ip}", context.GetHttpContext().Connection.RemoteIpAddress);
            }
            catch (AggregateException aggregateException) when(aggregateException.InnerException is IOException ioException && ioException.Message.StartsWith("The client reset the request stream"))
            {
                _logger.LogInformation("Client disconnected. ip: {Ip}", context.GetHttpContext().Connection.RemoteIpAddress);
            }
        });
    }
}