namespace Percolator.Infrastructure.Vault;

using Microsoft.Extensions.Hosting;
using Percolator.Domain.Security.Ports;

internal interface IStage2HostFactory
{
    IHost BuildAuthenticatedHost(IMasterKeyHandle masterKeyHandle);
}
