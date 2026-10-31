namespace Percolator.Infrastructure.Tests.Transport.Dispatching;

using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.Application2.Ports;
using Percolator.Domain.Identities.ValueObjects;
using Percolator.Infrastructure.Transport;

[TestFixture]
public sealed class StreamRegistryTests
{
    [Test]
    public async Task BackpressureSaturation_ReturnsBackpressured()
    {
        var registry = new StreamRegistry();
        var targetId = PublicIdentityId.New();

        // Register stream with small bounded capacity of 2
        var reader = registry.RegisterStream(targetId, capacity: 2);
        registry.HasActiveStream(targetId).Should().BeTrue();

        var payload1 = new byte[] { 1, 2, 3 };
        var payload2 = new byte[] { 4, 5, 6 };
        var payload3 = new byte[] { 7, 8, 9 };

        // 1st write succeeds
        var r1 = await registry.TryWriteAsync(targetId, payload1);
        r1.IsSuccess.Should().BeTrue();

        // 2nd write succeeds
        var r2 = await registry.TryWriteAsync(targetId, payload2);
        r2.IsSuccess.Should().BeTrue();

        // 3rd write causes queue buffer saturation -> Backpressured
        var r3 = await registry.TryWriteAsync(targetId, payload3);
        r3.IsSuccess.Should().BeFalse();
        r3.Status.Should().Be(StreamWriteStatus.Backpressured);

        // Read one item from stream reader
        bool readOk = reader.TryRead(out var readItem);
        readOk.Should().BeTrue();
        readItem.ToArray().Should().Equal(payload1);

        // Now queue has space -> write succeeds
        var r4 = await registry.TryWriteAsync(targetId, payload3);
        r4.IsSuccess.Should().BeTrue();

        // Unregister stream -> stream closed
        registry.UnregisterStream(targetId);
        registry.HasActiveStream(targetId).Should().BeFalse();

        var r5 = await registry.TryWriteAsync(targetId, payload1);
        r5.Status.Should().Be(StreamWriteStatus.StreamClosed);
    }
}
