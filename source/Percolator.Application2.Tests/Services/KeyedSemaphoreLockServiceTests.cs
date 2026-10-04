using Percolator.Application2.Services;
using Percolator.Domain.Channels.ValueObjects;

namespace Percolator.Application2.Tests.Services;

[TestFixture]
public sealed class KeyedSemaphoreLockServiceTests
{
    [Test]
    public async Task AcquireLockAsync_SameKey_EnforcesMutualExclusion()
    {
        var lockService = new KeyedSemaphoreLockService();
        var channelId = ChannelId.New();
        var sequence = new List<int>();

        var t1Entered = new TaskCompletionSource();
        var t1CanExit = new TaskCompletionSource();

        var t1 = Task.Run(async () =>
        {
            using (await lockService.AcquireLockAsync(channelId))
            {
                sequence.Add(1);
                t1Entered.SetResult();
                await t1CanExit.Task;
            }
        });

        await t1Entered.Task;

        var t2 = Task.Run(async () =>
        {
            using (await lockService.AcquireLockAsync(channelId))
            {
                sequence.Add(2);
            }
        });

        // Small delay to ensure t2 is waiting for lock
        await Task.Delay(50);
        sequence.Should().Equal(1);

        // Allow t1 to exit lock
        t1CanExit.SetResult();
        await Task.WhenAll(t1, t2);

        sequence.Should().Equal(1, 2);
    }

    [Test]
    public async Task AcquireLockAsync_DifferentKeys_CanExecuteConcurrently()
    {
        var lockService = new KeyedSemaphoreLockService();
        var ch1 = ChannelId.New();
        var ch2 = ChannelId.New();
        var t1Entered = new TaskCompletionSource();
        var t2Entered = new TaskCompletionSource();

        var t1 = Task.Run(async () =>
        {
            using (await lockService.AcquireLockAsync(ch1))
            {
                t1Entered.SetResult();
                await t2Entered.Task;
            }
        });

        var t2 = Task.Run(async () =>
        {
            using (await lockService.AcquireLockAsync(ch2))
            {
                t2Entered.SetResult();
                await t1Entered.Task;
            }
        });

        await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(2));
        t1.IsCompletedSuccessfully.Should().BeTrue();
        t2.IsCompletedSuccessfully.Should().BeTrue();
    }
}
