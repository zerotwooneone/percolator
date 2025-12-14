namespace Percolator.Application.Ingress;

public sealed record IngressResult(
    IngressDisposition Disposition,
    byte[]? ResponseBytes = null);
