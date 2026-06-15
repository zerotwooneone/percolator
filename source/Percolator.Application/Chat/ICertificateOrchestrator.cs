namespace Percolator.Application.Chat;

public interface ICertificateOrchestrator
{
    Task RefreshLocalCertificateAsync(CancellationToken ct);
}
