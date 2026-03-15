namespace Percolator.Network.ValueObjects;

public sealed class RetryBudget
{
    public int MaxAttempts { get; }
    public TimeSpan Window { get; }

    public RetryBudget(int maxAttempts, TimeSpan window)
    {
        MaxAttempts = maxAttempts;
        Window = window;
    }
}
