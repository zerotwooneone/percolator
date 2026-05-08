namespace Desktop.Wpf.Features.Self;

public interface IIdentityBootstrap
{
    Task BootstrapAsync(CancellationToken cancellationToken = default);
}
