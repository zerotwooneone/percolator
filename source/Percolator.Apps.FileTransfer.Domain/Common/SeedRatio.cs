using Percolator.Apps.FileTransfer.Domain.Exceptions;

namespace Percolator.Apps.FileTransfer.Domain.Common;

public readonly record struct SeedRatio(double Multiplier, bool IsUnlimited)
{
    public static SeedRatio Zero => new(0.0, false);
    public static SeedRatio Unlimited => new(0.0, true);

    public bool IsZero => !IsUnlimited && Multiplier == 0.0;

    public static SeedRatio FromMultiplier(double multiplier)
    {
        if (double.IsNaN(multiplier) || double.IsInfinity(multiplier) || multiplier < 0.0)
            throw new DomainValidationException($"Invalid seed ratio multiplier {multiplier}. Must be finite and non-negative.");
        return new SeedRatio(multiplier, false);
    }
}
