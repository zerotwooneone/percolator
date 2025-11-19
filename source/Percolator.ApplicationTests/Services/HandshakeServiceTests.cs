using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application.Services;
using Percolator.Cryptography;
using Percolator.Cryptography.Primitives;

namespace Percolator.ApplicationTests.Services;

[TestFixture]
public class HandshakeServiceTests
{
    private sealed class InMemorySessionRepo : ISessionRepository
    {
        private SecureSession? _session;
        public Task AddAsync(SecureSession s, CancellationToken ct = default)
        {
            _session = s; return Task.CompletedTask;
        }
        public Task<SecureSession?> GetAsync(SessionId id, CancellationToken ct = default)
        {
            return Task.FromResult(_session);
        }
        public Task UpdateAsync(SecureSession s, CancellationToken ct = default)
        {
            _session = s; return Task.CompletedTask;
        }
    }

    [Test]
    public async Task InitiateStandardHandshake_returns_session_and_initial_cipher_when_plaintext_provided()
    {
        var svc = new HandshakeService();
        var result = await svc.InitiateStandardHandshakeAsync(new PeerId(Guid.NewGuid()), new Plaintext(new byte[]{0xAA}), CancellationToken.None);
        result.SessionId.Should().NotBeNull();
        result.InitialCipher.Should().NotBeNull();
    }
}
