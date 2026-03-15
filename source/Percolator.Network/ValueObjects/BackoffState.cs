namespace Percolator.Network.ValueObjects;

public sealed class BackoffState
{
    public int Attempts { get; private set; }
    public DateTimeOffset? LastFailureAt { get; private set; }
    public DateTimeOffset? NextEligibleAt { get; private set; }

    public void RegisterFailure(DateTimeOffset now, TimeSpan backoff)
    {
        Attempts++;
        LastFailureAt = now;
        NextEligibleAt = now + backoff;
    }

    public bool CanAttempt(DateTimeOffset now, RetryBudget budget)
    {
        if (Attempts >= budget.MaxAttempts)
        {
            return false;
        }
        if (NextEligibleAt.HasValue && now < NextEligibleAt.Value)
        {
            return false;
        }
        return true;
    }
}
