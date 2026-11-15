using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Google.Protobuf;
using Grpc.Core;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Protos;

namespace Percolator.Infrastructure.Transport;

/// <summary>
/// <see cref="IRelayMailboxClient"/> over the authenticated relay gRPC surface.
/// <see cref="ConnectMailboxStream"/> is a duplex stream: the relay issues a nonce
/// challenge, the client proves drain-token possession via HMAC-SHA256, then the relay
/// replays queued envelopes and pushes live ones until the stream ends or the client
/// completes the request stream.
/// </summary>
public sealed class GrpcRelayMailboxClient : IRelayMailboxClient
{
    private static readonly TimeSpan EnvelopeExpiryWindow = TimeSpan.FromDays(7);

    private readonly Func<RelayEndpoint, AuthenticatedRelayService.AuthenticatedRelayServiceClient> _clientFactory;
    private readonly IDateTimeProvider _timeProvider;

    public GrpcRelayMailboxClient(
        IPeerGrpcChannelFactory channelFactory,
        IDateTimeProvider timeProvider)
        : this(
            endpoint =>
            {
                ArgumentNullException.ThrowIfNull(endpoint);
                return new AuthenticatedRelayService.AuthenticatedRelayServiceClient(
                    channelFactory.GetOrCreateChannel(new Uri($"https://{endpoint.Value}")));
            },
            timeProvider)
    {
        ArgumentNullException.ThrowIfNull(channelFactory);
    }

    /// <summary>Internal constructor for injecting a custom client factory (e.g. test invokers).</summary>
    internal GrpcRelayMailboxClient(
        Func<RelayEndpoint, AuthenticatedRelayService.AuthenticatedRelayServiceClient> clientFactory,
        IDateTimeProvider timeProvider)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<DomainResult<IMailboxStreamSession>> OpenMailboxStreamAsync(
        RelayEndpoint endpoint,
        MailboxTokenTriad tokens,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(tokens);

        AsyncDuplexStreamingCall<MailboxStreamClientMessage, MailboxStreamServerMessage>? call = null;
        try
        {
            var client = _clientFactory(endpoint);
            call = client.ConnectMailboxStream(cancellationToken: ct);

            // 1. Server sends the nonce challenge first.
            if (!await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                call.Dispose();
                return Failure("RELAY_AUTH_FAILED", "Relay closed the stream before issuing an auth challenge.");
            }

            var challenge = call.ResponseStream.Current;
            if (challenge.PayloadCase != MailboxStreamServerMessage.PayloadOneofCase.AuthChallenge)
            {
                call.Dispose();
                return Failure("RELAY_AUTH_FAILED",
                    $"Expected auth challenge, received {challenge.PayloadCase}.");
            }

            // 2. Prove drain-token possession: HMAC-SHA256(drain_token, nonce).
            var hmac = HMACSHA256.HashData(tokens.DrainToken.Span, challenge.AuthChallenge.Nonce.Span);
            await call.RequestStream.WriteAsync(new MailboxStreamClientMessage
            {
                AuthResponse = new AuthResponseProto
                {
                    RoutingToken = ByteString.CopyFrom(tokens.RoutingToken.ToByteArray()),
                    ChallengeHmac = ByteString.CopyFrom(hmac)
                }
            }, ct).ConfigureAwait(false);

            // 3. Read the auth result; envelopes follow on success.
            if (!await call.ResponseStream.MoveNext(ct).ConfigureAwait(false))
            {
                call.Dispose();
                return Failure("RELAY_AUTH_FAILED", "Relay closed the stream before returning an auth result.");
            }

            var result = call.ResponseStream.Current;
            if (result.PayloadCase != MailboxStreamServerMessage.PayloadOneofCase.AuthResult ||
                result.AuthResult is not { Authenticated: true })
            {
                var reason = result.AuthResult?.Reason;
                call.Dispose();
                return Failure("RELAY_AUTH_FAILED",
                    string.IsNullOrEmpty(reason) ? "Mailbox stream authentication failed." : reason);
            }

            var session = new GrpcMailboxStreamSession(call, tokens.RoutingToken, _timeProvider);
            return DomainResult<IMailboxStreamSession>.Success(session);
        }
        catch (RpcException ex)
        {
            call?.Dispose();
            return Failure("STREAM_OPEN_FAILED", ex.Status.Detail ?? ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            call?.Dispose();
            return Failure("STREAM_OPEN_FAILED", ex.Message);
        }
        catch (UriFormatException ex)
        {
            call?.Dispose();
            return Failure("STREAM_OPEN_FAILED", ex.Message);
        }
        catch (ArgumentException ex)
        {
            call?.Dispose();
            return Failure("STREAM_OPEN_FAILED", ex.Message);
        }
    }

    private static DomainResult<IMailboxStreamSession> Failure(string code, string description)
        => DomainResult<IMailboxStreamSession>.Failure(new DomainError(code, description));

    /// <summary>
    /// Live stream session: a pump task copies server envelopes into a bounded channel
    /// (backpressure: the pump pauses when the channel is full), while
    /// <see cref="AcknowledgeAsync"/> writes acks through a serialized write gate.
    /// </summary>
    private sealed class GrpcMailboxStreamSession : IMailboxStreamSession
    {
        private const int ChannelCapacity = 256;

        private readonly AsyncDuplexStreamingCall<MailboxStreamClientMessage, MailboxStreamServerMessage> _call;
        private readonly Channel<QueuedMailboxEnvelope> _channel;
        private readonly SemaphoreSlim _writeGate = new(1, 1);
        private readonly CancellationTokenSource _pumpCts = new();
        private readonly Task _pump;
        private int _disposed;

        public GrpcMailboxStreamSession(
            AsyncDuplexStreamingCall<MailboxStreamClientMessage, MailboxStreamServerMessage> call,
            BlindedRoutingToken routingToken,
            IDateTimeProvider timeProvider)
        {
            _call = call;
            _channel = Channel.CreateBounded<QueuedMailboxEnvelope>(
                new BoundedChannelOptions(ChannelCapacity)
                {
                    SingleWriter = true,
                    SingleReader = true,
                    FullMode = BoundedChannelFullMode.Wait
                });
            _pump = Task.Run(() => PumpAsync(routingToken, timeProvider));
        }

        public IAsyncEnumerable<QueuedMailboxEnvelope> Envelopes => _channel.Reader.ReadAllAsync();

        public async Task<DomainResult> AcknowledgeAsync(MailboxQueueId queueId, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(queueId);

            await _writeGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _call.RequestStream.WriteAsync(new MailboxStreamClientMessage
                {
                    Ack = new MailboxAckProto { QueueId = queueId.Value, Acknowledged = true }
                }, ct).ConfigureAwait(false);
                return DomainResult.Success();
            }
            catch (RpcException ex)
            {
                return DomainResult.Failure(
                    new DomainError("STREAM_ACK_FAILED", ex.Status.Detail ?? ex.Message));
            }
            catch (InvalidOperationException ex)
            {
                return DomainResult.Failure(
                    new DomainError("STREAM_ACK_FAILED", ex.Message));
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private async Task PumpAsync(BlindedRoutingToken routingToken, IDateTimeProvider timeProvider)
        {
            Exception? fault = null;
            try
            {
                while (await _call.ResponseStream.MoveNext(_pumpCts.Token).ConfigureAwait(false))
                {
                    var msg = _call.ResponseStream.Current;
                    if (msg.PayloadCase != MailboxStreamServerMessage.PayloadOneofCase.Envelope ||
                        msg.Envelope is null)
                    {
                        continue;
                    }

                    SealedEnvelopeProto sealedEnv;
                    try
                    {
                        sealedEnv = SealedEnvelopeProto.Parser.ParseFrom(msg.Envelope.EnvelopePayload.Span);
                    }
                    catch (InvalidProtocolBufferException)
                    {
                        // Malformed envelope — drop it; the pump continues and the row
                        // stays queued relay-side until a terminal-failure ack (DRAIN-05).
                        continue;
                    }

                    var now = timeProvider.UtcNow;
                    var enqueuedAt = msg.Envelope.EnqueuedAtUtc?.ToDateTimeOffset() ?? now;
                    var expiry = ClampExpiry(sealedEnv.ExpiresAtUtc?.ToDateTimeOffset(), now);
                    var ciphertext = sealedEnv.EncryptedPackage.ToByteArray();

                    var queueId = MailboxQueueId.Create(msg.Envelope.QueueId);
                    if (queueId.IsFailure)
                    {
                        // Unparseable relay key — we could never ack it; drop and continue.
                        continue;
                    }

                    var envelope = new MailboxEnvelope(
                        EnvelopeId.New(), routingToken, ciphertext, enqueuedAt, expiry);
                    await _channel.Writer.WriteAsync(
                        new QueuedMailboxEnvelope(queueId.Value!, envelope),
                        _pumpCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_pumpCts.IsCancellationRequested)
            {
                // Dispose path — expected clean teardown.
            }
            catch (RpcException ex) when (_pumpCts.IsCancellationRequested || ex.StatusCode == StatusCode.Cancelled)
            {
                // Dispose path or cancelled call — expected clean teardown.
            }
            catch (RpcException ex)
            {
                // Stream fault — propagate to channel writer so reader/supervisor observes failure and retries.
                fault = ex;
            }
            catch (InvalidOperationException ex)
            {
                // Disposed/completed request stream torn down under us or transport fault.
                if (!_pumpCts.IsCancellationRequested)
                {
                    fault = ex;
                }
            }
            catch (Exception ex)
            {
                // Unexpected transport or parsing fault.
                fault = ex;
            }
            finally
            {
                _channel.Writer.TryComplete(fault);
            }
        }

        private static DateTimeOffset ClampExpiry(DateTimeOffset? relayExpiry, DateTimeOffset now)
        {
            var windowExpiry = now + EnvelopeExpiryWindow;
            return relayExpiry is { } e && e < windowExpiry ? e : windowExpiry;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await _call.RequestStream.CompleteAsync().ConfigureAwait(false);
            }
            catch
            {
                // Best-effort — the relay may already have closed its side.
            }

            _pumpCts.Cancel();
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch
            {
                // Pump faults are already contained; teardown continues.
            }

            // Volatile zeroization (DEP-INFRA-VOLATILE-ZEROIZATION): wipe undelivered
            // ciphertext left in the channel before releasing the session.
            while (_channel.Reader.TryRead(out var item))
            {
                CryptographicOperations.ZeroMemory(
                    MemoryMarshal.AsMemory(item.Envelope.Ciphertext).Span);
            }

            _call.Dispose();
            _writeGate.Dispose();
            _pumpCts.Dispose();
        }
    }
}
