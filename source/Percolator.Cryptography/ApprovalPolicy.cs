namespace Percolator.Cryptography;

public sealed class ApprovalPolicy
{
    public static readonly ApprovalPolicy Default = new ApprovalPolicy(allowAutoRespond: false);

    public bool AllowAutoRespond { get; }

    public ApprovalPolicy(bool allowAutoRespond)
    {
        AllowAutoRespond = allowAutoRespond;
    }
}
