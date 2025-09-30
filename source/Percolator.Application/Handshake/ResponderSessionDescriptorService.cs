using System;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Percolator.Contracts;

namespace Percolator.Application.Handshake
{
    public interface IResponderSessionDescriptorService
    {
        Task<HandshakeResponderHello> CreateAsync(
            byte[] responderEphemeralKeySpki,
            string directSessionId,
            CancellationToken ct = default);

        Task<(byte[] ephemeralKeySpki, string directSessionId)?> VerifyAndParseAsync(
            HandshakeResponderHello hello,
            CancellationToken ct = default);
    }

    internal sealed class ResponderSessionDescriptorService : IResponderSessionDescriptorService
    {

        public Task<HandshakeResponderHello> CreateAsync(
            byte[] responderEphemeralKeySpki,
            string directSessionId,
            CancellationToken ct = default)
        {
            if (responderEphemeralKeySpki is null || responderEphemeralKeySpki.Length == 0)
                throw new ArgumentException("Responder ephemeral key is required", nameof(responderEphemeralKeySpki));
            if (string.IsNullOrWhiteSpace(directSessionId))
                throw new ArgumentException("Direct session id is required", nameof(directSessionId));

            var hello = new HandshakeResponderHello
            {
                Version = 1,
                ResponderEphemeralKey = ByteString.CopyFrom(responderEphemeralKeySpki),
                DirectSessionId = directSessionId
            };
            return Task.FromResult(hello);
        }

        public Task<(byte[] ephemeralKeySpki, string directSessionId)?> VerifyAndParseAsync(
            HandshakeResponderHello hello,
            CancellationToken ct = default)
        {
            if (hello is null) throw new ArgumentNullException(nameof(hello));
            if (!hello.HasResponderEphemeralKey || !hello.HasDirectSessionId)
                return Task.FromResult<(byte[] ephemeralKeySpki, string directSessionId)?>(null);
            return Task.FromResult<(byte[] ephemeralKeySpki, string directSessionId)?>(
                (hello.ResponderEphemeralKey.ToByteArray(), hello.DirectSessionId));
        }
    }
}
