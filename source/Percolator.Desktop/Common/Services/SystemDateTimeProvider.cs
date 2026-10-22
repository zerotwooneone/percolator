using System;
using Percolator.Domain.Common;

namespace Percolator.Desktop.Common.Services;

public sealed class SystemDateTimeProvider : IDateTimeProvider
{
    public static readonly SystemDateTimeProvider Instance = new();

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
