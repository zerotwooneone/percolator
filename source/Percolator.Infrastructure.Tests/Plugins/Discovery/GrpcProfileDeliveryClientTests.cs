namespace Percolator.Infrastructure.Tests.Plugins.Discovery;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using NUnit.Framework;
using Percolator.Domain.Delivery.ValueObjects;
using Percolator.Infrastructure.Protos;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class GrpcProfileDeliveryClientTests
{
    private sealed class TestProfileCallInvoker : CallInvoker
    {
        public Func<ProfileFetchRequestProto, ProfileFetchResponseProto>? OnGetProfile { get; set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            if (request is ProfileFetchRequestProto fetchReq && OnGetProfile != null)
            {
                var resp = OnGetProfile(fetchReq);
                return new AsyncUnaryCall<TResponse>(
                    Task.FromResult((TResponse)(object)resp),
                    Task.FromResult(new Metadata()),
                    () => Status.DefaultSuccess,
                    () => new Metadata(),
                    () => { });
            }

            throw new NotImplementedException();
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotImplementedException();

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotImplementedException();

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) =>
            throw new NotImplementedException();

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) =>
            throw new NotImplementedException();
    }

    [Test]
    public async Task BatchProfileFetch_SinglePeerFailure_DoesNotAbortRemainingPeers()
    {
        byte[] locABytes = new byte[32];
        byte[] locBBytes = new byte[32];
        RandomNumberGenerator.Fill(locABytes);
        RandomNumberGenerator.Fill(locBBytes);

        var locatorA = BlindedProfileLocator.FromSpan(locABytes);
        var locatorB = BlindedProfileLocator.FromSpan(locBBytes);

        byte[] validNonce = new byte[12];
        byte[] validCiphertext = new byte[1040];
        RandomNumberGenerator.Fill(validNonce);
        RandomNumberGenerator.Fill(validCiphertext);

        var invoker = new TestProfileCallInvoker
        {
            OnGetProfile = req =>
            {
                if (req.BlindedLocator.Span.SequenceEqual(locatorA.Span))
                {
                    // Peer A fails with network/RPC error
                    throw new RpcException(new Status(StatusCode.Unavailable, "Peer A endpoint is offline"));
                }

                if (req.BlindedLocator.Span.SequenceEqual(locatorB.Span))
                {
                    // Peer B succeeds
                    return new ProfileFetchResponseProto
                    {
                        Found = true,
                        Nonce = ByteString.CopyFrom(validNonce),
                        Ciphertext = ByteString.CopyFrom(validCiphertext),
                        Revision = 1,
                        ExpiresAtUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow.AddDays(7))
                    };
                }

                return new ProfileFetchResponseProto { Found = false };
            }
        };

        var grpcClient = new UnauthenticatedDeliveryService.UnauthenticatedDeliveryServiceClient(invoker);
        var deliveryClient = new GrpcProfileDeliveryClient(grpcClient);

        var targets = new List<(Uri? Endpoint, BlindedProfileLocator Locator)>
        {
            (new Uri("https://relay-a.percolator.net:443"), locatorA),
            (new Uri("https://relay-b.percolator.net:443"), locatorB)
        };

        // Act
        var results = await deliveryClient.BatchFetchProfilesAsync(targets);

        // Assert: Fault tolerance - batch did not throw or abort, both entries exist
        results.Should().HaveCount(2);

        // Target A failed
        results.Should().ContainKey(locatorA);
        results[locatorA].IsFailure.Should().BeTrue();
        results[locatorA].Error!.Code.Should().Be("PROFILE_RPC_ERROR");

        // Target B succeeded despite Target A failing
        results.Should().ContainKey(locatorB);
        results[locatorB].IsSuccess.Should().BeTrue();
        results[locatorB].Value.Nonce.Should().Equal(validNonce);
        results[locatorB].Value.Ciphertext.Should().Equal(validCiphertext);
    }
}
