using Percolator.Network;
using Percolator.Network.Egress;
using Percolator.Network.ValueObjects;

namespace Percolator.NetworkTests.Egress;

[TestFixture]
public class NetworkEgressJobTests
{
    [Test]
    public void RecordFailure_IncrementsAttemptAndSetsNextAttemptUtc()
    {
        // ARRANGE
        var jobId = new EgressJobId(1);
        var destinationPeerId = new NetworkPeerId(1);
        var payloadBytes = NetworkPayloadBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        
        var job = new NetworkEgressJob(
            jobId,
            destinationPeerId,
            RoutePreference.Relay,
            PayloadType.Group,
            payloadBytes,
            fixedTime);

        // ACT
        job.RecordFailure(fixedTime);

        // ASSERT
        Assert.That(job.AttemptCount, Is.EqualTo(1));
        // Exponential backoff: 2^1 = 2 seconds
        Assert.That(job.NextAttemptUtc, Is.EqualTo(fixedTime.AddSeconds(2)));
    }

    [Test]
    public void RecordFailure_SecondFailure_UsesExponentialBackoff()
    {
        // ARRANGE
        var jobId = new EgressJobId(1);
        var destinationPeerId = new NetworkPeerId(1);
        var payloadBytes = NetworkPayloadBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        
        var job = new NetworkEgressJob(
            jobId,
            destinationPeerId,
            RoutePreference.Relay,
            PayloadType.Group,
            payloadBytes,
            fixedTime);

        // ACT
        job.RecordFailure(fixedTime);
        job.RecordFailure(fixedTime);

        // ASSERT
        Assert.That(job.AttemptCount, Is.EqualTo(2));
        // Exponential backoff: 2^2 = 4 seconds
        Assert.That(job.NextAttemptUtc, Is.EqualTo(fixedTime.AddSeconds(4)));
    }

    [Test]
    public void RecordFailure_ReachesMaxAttempts_MarksPermanentlyFailed()
    {
        // ARRANGE
        var jobId = new EgressJobId(1);
        var destinationPeerId = new NetworkPeerId(1);
        var payloadBytes = NetworkPayloadBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        
        var job = new NetworkEgressJob(
            jobId,
            destinationPeerId,
            RoutePreference.Relay,
            PayloadType.Group,
            payloadBytes,
            fixedTime);

        // ACT - Record failure 10 times (max attempts)
        for (int i = 0; i < 10; i++)
        {
            job.RecordFailure(fixedTime);
        }

        // ASSERT
        Assert.That(job.IsPermanentlyFailed, Is.True);
    }

    [Test]
    public void MarkSent_UpdatesStateToSent()
    {
        // ARRANGE
        var jobId = new EgressJobId(1);
        var destinationPeerId = new NetworkPeerId(1);
        var payloadBytes = NetworkPayloadBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        
        var job = new NetworkEgressJob(
            jobId,
            destinationPeerId,
            RoutePreference.Relay,
            PayloadType.Group,
            payloadBytes,
            fixedTime);

        // ACT
        job.MarkSent();

        // ASSERT
        Assert.That(job.IsSent, Is.True);
    }

    [Test]
    public void MarkSent_AfterPermanentFailure_DoesNotUpdateState()
    {
        // ARRANGE
        var jobId = new EgressJobId(1);
        var destinationPeerId = new NetworkPeerId(1);
        var payloadBytes = NetworkPayloadBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        
        var job = new NetworkEgressJob(
            jobId,
            destinationPeerId,
            RoutePreference.Relay,
            PayloadType.Group,
            payloadBytes,
            fixedTime);

        // ACT - Mark as permanently failed first
        for (int i = 0; i < 10; i++)
        {
            job.RecordFailure(fixedTime);
        }
        job.MarkSent();

        // ASSERT - Should remain permanently failed, not marked as sent
        Assert.That(job.IsPermanentlyFailed, Is.True);
        Assert.That(job.IsSent, Is.False);
    }

    [Test]
    public void RecordFailure_AfterMarkSent_DoesNotUpdateState()
    {
        // ARRANGE
        var jobId = new EgressJobId(1);
        var destinationPeerId = new NetworkPeerId(1);
        var payloadBytes = NetworkPayloadBytes.FromBytesOwned(new byte[] { 1, 2, 3 });
        var fixedTime = new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);
        
        var job = new NetworkEgressJob(
            jobId,
            destinationPeerId,
            RoutePreference.Relay,
            PayloadType.Group,
            payloadBytes,
            fixedTime);

        // ACT - Mark as sent first
        job.MarkSent();
        job.RecordFailure(fixedTime);

        // ASSERT - Should remain sent, attempt count should not change
        Assert.That(job.IsSent, Is.True);
        Assert.That(job.AttemptCount, Is.EqualTo(0));
    }
}
