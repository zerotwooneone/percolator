using Percolator.Domain.Common;

namespace Percolator.Application2.Tests.TestDoubles;

public sealed class TestDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}
