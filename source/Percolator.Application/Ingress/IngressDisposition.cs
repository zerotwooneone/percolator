namespace Percolator.Application.Ingress;

public enum IngressDisposition
{
    Accepted = 0,
    Rejected_NotReady = 1,
    Rejected_Invalid = 2,
    Rejected_Unsupported = 3,
    Failed = 4
}
