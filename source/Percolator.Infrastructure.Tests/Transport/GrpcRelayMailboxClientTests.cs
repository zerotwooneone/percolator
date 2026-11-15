namespace Percolator.Infrastructure.Tests.Transport;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Application.Delivery.Ports;
using Percolator.Domain.Common;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class GrpcRelayMailboxClientTests
{
    private sealed class FixedTimeProvider : IDateTimeProvider
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class RecordingRequestStreamWriter : IClientStreamWriter<MailboxStreamClientMessage>
    {
        public List<MailboxStreamClientMessage> Messages { get; } = new();
        public bool Completed { get; private set; }
        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(MailboxStreamClientMessage message)
        {
            Messages.Add(message);
            return Task.CompletedTask;
        }

        public Task CompleteAsync()
        {
            Completed = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ScriptedResponseStreamReader : IAsyncStreamReader<MailboxStreamServerMessage>
    {
        private readonly Queue<MailboxStreamServerMessage> _messages;
        private readonly bool _blockAtEnd;

        public ScriptedResponseStreamReader(IEnumerable<MailboxStreamServerMessage> messages, bool blockAtEnd)
        {
            _messages = new Queue<MailboxStreamServerMessage>(messages);
            _blockAtEnd = blockAtEnd;
        }

        public MailboxStreamServerMessage Current { get; private set; } = null!;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_messages.Count > 0)
            {
                Current = _messages.Dequeue();
                return true;
            }

            if (_blockAtEnd)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return false;
        }
    }

    private sealed class TestMailboxCallInvoker : CallInvoker
    {
        public RecordingRequestStreamWriter RequestWriter { get; } = new();
        public List<MailboxStreamServerMessage> Script { get; } = new();
        public bool BlockAtEnd { get; set; } = true;

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options)
        {
            var writer = (IClientStreamWriter<TRequest>)(object)RequestWriter;
            var reader = (IAsyncStreamReader<TResponse>)(object)new ScriptedResponseStreamReader(Script, BlockAtEnd);
            return new AsyncDuplexStreamingCall<TRequest, TResponse>(
                writer,
                reader,
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => new Metadata(),
                () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotImplementedException();

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotImplementedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotImplementedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotImplementedException();
    }

    private static readonly RelayEndpoint Endpoint = RelayEndpoint.FromHostAndPort("127.0.0.1", 1);

    private static MailboxStreamServerMessage Challenge(byte[] nonce) => new()
    {
        AuthChallenge = new AuthChallengeProto { Nonce = ByteString.CopyFrom(nonce) }
    };

    private static MailboxStreamServerMessage AuthResult(bool authenticated, string reason = "") => new()
    {
        AuthResult = new AuthResultProto { Authenticated = authenticated, Reason = reason }
    };

    private static MailboxStreamServerMessage Envelope(string queueId, byte[] ciphertext) => new()
    {
        Envelope = new QueuedSealedEnvelopeProto
        {
            QueueId = queueId,
            EnvelopePayload = new SealedEnvelopeProto
            {
                EncryptedPackage = ByteString.CopyFrom(ciphertext)
            }.ToByteString(),
            EnqueuedAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow)
        }
    };

    private static GrpcRelayMailboxClient CreateClient(TestMailboxCallInvoker invoker)
        => new(new AuthenticatedRelayService.AuthenticatedRelayServiceClient(invoker), new FixedTimeProvider());

    private static (MailboxTokenTriad Tokens, byte[] DrainBytes) CreateTokens()
    {
        var drainBytes = new byte[32];
        var deliveryBytes = new byte[32];
        RandomNumberGenerator.Fill(drainBytes);
        RandomNumberGenerator.Fill(deliveryBytes);
        var tokens = new MailboxTokenTriad(
            BlindedRoutingToken.New(),
            DeliveryToken.FromSpan(deliveryBytes),
            DeliveryToken.FromSpan(drainBytes));
        return (tokens, drainBytes);
    }

    [Test]
    public async Task OpenMailboxStreamAsync_AuthenticatesWithDrainTokenHmac()
    {
        var nonce = new byte[32];
        RandomNumberGenerator.Fill(nonce);
        var invoker = new TestMailboxCallInvoker();
        invoker.Script.Add(Challenge(nonce));
        invoker.Script.Add(AuthResult(authenticated: true));

        var (tokens, drainBytes) = CreateTokens();
        var client = CreateClient(invoker);

        var result = await client.OpenMailboxStreamAsync(Endpoint, tokens);

        result.IsSuccess.Should().BeTrue();
        await using var session = result.Value!;

        var auth = invoker.RequestWriter.Messages.Should().ContainSingle().Subject.AuthResponse;
        auth.RoutingToken.ToByteArray().Should().Equal(tokens.RoutingToken.ToByteArray());
        auth.ChallengeHmac.ToByteArray().Should().Equal(HMACSHA256.HashData(drainBytes, nonce));
    }

    [Test]
    public async Task OpenMailboxStreamAsync_AuthRejected_ReturnsRelayAuthFailed()
    {
        var invoker = new TestMailboxCallInvoker();
        invoker.Script.Add(Challenge(new byte[32]));
        invoker.Script.Add(AuthResult(authenticated: false, reason: "drain token mismatch"));

        var (tokens, _) = CreateTokens();
        var client = CreateClient(invoker);

        var result = await client.OpenMailboxStreamAsync(Endpoint, tokens);

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("RELAY_AUTH_FAILED");
        result.Error.Description.Should().Be("drain token mismatch");
    }

    [Test]
    public async Task Session_Envelopes_YieldsQueuedEnvelopeWithRelayQueueId()
    {
        var ciphertext = new byte[] { 1, 2, 3, 4 };
        var invoker = new TestMailboxCallInvoker { BlockAtEnd = false };
        invoker.Script.Add(Challenge(new byte[32]));
        invoker.Script.Add(AuthResult(authenticated: true));
        invoker.Script.Add(Envelope("q-1", ciphertext));

        var (tokens, _) = CreateTokens();
        var client = CreateClient(invoker);
        var openResult = await client.OpenMailboxStreamAsync(Endpoint, tokens);
        await using var session = openResult.Value!;

        var received = new List<QueuedMailboxEnvelope>();
        await foreach (var item in session.Envelopes)
        {
            received.Add(item);
        }

        received.Should().ContainSingle();
        received[0].QueueId.Value.Should().Be("q-1");
        received[0].Envelope.Ciphertext.ToArray().Should().Equal(ciphertext);
    }

    [Test]
    public async Task Session_EnvelopeWithUnparseableQueueId_IsDropped()
    {
        var invoker = new TestMailboxCallInvoker { BlockAtEnd = false };
        invoker.Script.Add(Challenge(new byte[32]));
        invoker.Script.Add(AuthResult(authenticated: true));
        invoker.Script.Add(Envelope(string.Empty, new byte[] { 9 }));

        var (tokens, _) = CreateTokens();
        var client = CreateClient(invoker);
        var openResult = await client.OpenMailboxStreamAsync(Endpoint, tokens);
        await using var session = openResult.Value!;

        var received = new List<QueuedMailboxEnvelope>();
        await foreach (var item in session.Envelopes)
        {
            received.Add(item);
        }

        received.Should().BeEmpty("an unparseable queue id can never be acked");
    }

    [Test]
    public async Task AcknowledgeAsync_WritesAckProto()
    {
        var invoker = new TestMailboxCallInvoker();
        invoker.Script.Add(Challenge(new byte[32]));
        invoker.Script.Add(AuthResult(authenticated: true));

        var (tokens, _) = CreateTokens();
        var client = CreateClient(invoker);
        var openResult = await client.OpenMailboxStreamAsync(Endpoint, tokens);
        await using var session = openResult.Value!;

        var ack = await session.AcknowledgeAsync(MailboxQueueId.Parse("q-9"));

        ack.IsSuccess.Should().BeTrue();
        var ackMessage = invoker.RequestWriter.Messages.Single(m => m.Ack != null);
        ackMessage.Ack.QueueId.Should().Be("q-9");
        ackMessage.Ack.Acknowledged.Should().BeTrue();
    }

    [Test]
    public async Task DisposeAsync_CompletesRequestStream()
    {
        var invoker = new TestMailboxCallInvoker();
        invoker.Script.Add(Challenge(new byte[32]));
        invoker.Script.Add(AuthResult(authenticated: true));

        var (tokens, _) = CreateTokens();
        var client = CreateClient(invoker);
        var openResult = await client.OpenMailboxStreamAsync(Endpoint, tokens);
        var session = openResult.Value!;

        await session.DisposeAsync();

        invoker.RequestWriter.Completed.Should().BeTrue();
    }
}
