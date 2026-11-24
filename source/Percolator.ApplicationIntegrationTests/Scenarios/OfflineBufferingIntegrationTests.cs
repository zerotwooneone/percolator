namespace Percolator.ApplicationIntegrationTests.Scenarios;

using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using Percolator.ApplicationIntegrationTests.Harness;
using Percolator.Domain.Channels.ValueObjects;

[TestFixture]
public sealed class OfflineBufferingIntegrationTests
{
    private TestNetworkTopology _topology = null!;

    [SetUp]
    public void SetUp()
    {
        _topology = new TestNetworkTopology();
    }

    [TearDown]
    public async Task TearDown()
    {
        await _topology.DisposeAsync().ConfigureAwait(false);
    }

    [Test]
    public async Task OfflineBuffering_And_OutboxOutageRetry_EndToEnd()
    {
        // 1. Start Relay R1
        var relay = await _topology.CreateRelayAsync().ConfigureAwait(false);

        // 2. Provision Alice and Bob
        var alice = await _topology.CreatePersonaAsync("Alice").ConfigureAwait(false);
        var bob = await _topology.CreatePersonaAsync("Bob").ConfigureAwait(false);

        await alice.LinkRelayAsync(relay).ConfigureAwait(false);
        await bob.LinkRelayAsync(relay).ConfigureAwait(false);

        // 3. Pair personas via shared Exchange
        await _topology.PairPersonasAsync(bob, alice, relay).ConfigureAwait(false);

        var aliceChannelId = ChannelId.DeterministicForDirect(alice.IdentityId, bob.IdentityId, relay.Endpoint);
        var bobChannelId = ChannelId.DeterministicForDirect(bob.IdentityId, alice.IdentityId, relay.Endpoint);

        // 4. Bob pauses drain
        await bob.StopDrainAsync().ConfigureAwait(false);

        // 5. Alice sends 3 messages via R1
        await alice.SendChatAsync(aliceChannelId, "Msg 1").ConfigureAwait(false);
        await alice.SendChatAsync(aliceChannelId, "Msg 2").ConfigureAwait(false);
        await alice.SendChatAsync(aliceChannelId, "Msg 3").ConfigureAwait(false);

        // 6. Verify R1's SQLite mailbox queue depth == 3
        var bobRoutingToken = await bob.GetRoutingTokenAsync(relay).ConfigureAwait(false);

        using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50)))
        {
            int depth = 0;
            while (!cts.IsCancellationRequested)
            {
                depth = await relay.GetQueueDepthAsync(bobRoutingToken).ConfigureAwait(false);
                if (depth == 3) break;
                try
                {
                    if (!await timer.WaitForNextTickAsync(cts.Token).ConfigureAwait(false)) break;
                }
                catch (OperationCanceledException) { break; }
            }
            depth.Should().Be(3, "all 3 messages should be buffered in Bob's mailbox on R1");
        }

        // 7. Bob resumes drain; drains, decrypts all 3 messages in order; queue depth returns to 0
        await bob.ResumeDrainAsync().ConfigureAwait(false);

        var msg1 = await bob.WaitForMessageAsync(bobChannelId, "Msg 1", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var msg2 = await bob.WaitForMessageAsync(bobChannelId, "Msg 2", TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        var msg3 = await bob.WaitForMessageAsync(bobChannelId, "Msg 3", TimeSpan.FromSeconds(10)).ConfigureAwait(false);

        msg1.Plaintext.Should().Be("Msg 1");
        msg2.Plaintext.Should().Be("Msg 2");
        msg3.Plaintext.Should().Be("Msg 3");

        using (var ctsZero = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
        using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50)))
        {
            int depth = -1;
            while (!ctsZero.IsCancellationRequested)
            {
                depth = await relay.GetQueueDepthAsync(bobRoutingToken).ConfigureAwait(false);
                if (depth == 0) break;
                try
                {
                    if (!await timer.WaitForNextTickAsync(ctsZero.Token).ConfigureAwait(false)) break;
                }
                catch (OperationCanceledException) { break; }
            }
            depth.Should().Be(0, "mailbox queue should be fully drained");
        }

        // 8. Outbox Outage Retry: Stop R1
        await relay.StopAsync().ConfigureAwait(false);

        // Alice sends "Msg 4" while relay is stopped
        await alice.SendChatAsync(aliceChannelId, "Msg 4").ConfigureAwait(false);

        // Wait deterministically for Alice's outbox worker to attempt delivery, fail, and record retry
        await alice.WaitForOutboxRetryAsync(TimeSpan.FromSeconds(10), minRetryCount: 1).ConfigureAwait(false);

        // Restart R1
        await relay.RestartAsync().ConfigureAwait(false);

        // Alice's outbox retries, delivers to R1; Bob drains and decrypts "Msg 4"
        var msg4 = await bob.WaitForMessageAsync(bobChannelId, "Msg 4", TimeSpan.FromSeconds(15)).ConfigureAwait(false);
        msg4.Plaintext.Should().Be("Msg 4");
    }
}
