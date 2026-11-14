using Percolator.Domain.Common;

namespace Percolator.Application.Tests.TestDoubles;

public sealed class TestDateTimeProvider : IDateTimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}
