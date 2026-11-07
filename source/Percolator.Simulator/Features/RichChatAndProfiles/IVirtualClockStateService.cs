using System;
using System.Threading;
using System.Threading.Tasks;
using Percolator.Domain.Common;
using R3;

namespace Percolator.Simulator.Features.RichChatAndProfiles;

internal interface IVirtualClockStateService : IDateTimeProvider
{
    ReadOnlyReactiveProperty<DateTimeOffset> CurrentUtcNow { get; }
    int CurrentDiscoveryEpoch { get; }
    void AdvanceTime(TimeSpan delta);
    Task AdvanceTimeAsync(TimeSpan delta, CancellationToken ct = default);
}
