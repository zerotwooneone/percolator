using Microsoft.EntityFrameworkCore;
using Percolator.Application.Identity;
using Percolator.Identity;
using Percolator.Identity.Model;
using Percolator.Infrastructure.Persistence;

namespace Percolator.InfrastructureTests.Common;

public static class TestDb
{
    public static ActiveIdentityContext CreateActiveIdentity(int selfIdentityId = 1, Guid? identityGuid = null, string? name = null)
    {
        var active = new ActiveIdentityContext();
        var idGuid = identityGuid ?? Guid.NewGuid();
        var display = name ?? $"test-{selfIdentityId}";
        var identity = new IdentityRecord(new SelfId((uint)selfIdentityId), new PublicIdentityId(idGuid), new DeviceId(1), display) { ListeningPort = new Percolator.Identity.Model.ListeningPort(5000) };
        active.SetActiveIdentity(identity, null);
        return active;
    }

    public static PercolatorDbContext NewContext(DbContextOptions<PercolatorDbContext> options, int? selfIdentityId = 1)
    {
        // ActiveIdentityContext constructor removed - query filters are gone
        return new PercolatorDbContext(options);
    }

    public static PercolatorDbContext NewContextWithSchema(DbContextOptions<PercolatorDbContext> options, int? selfIdentityId = 1)
    {
        // Create schema without active identity
        using var schemaCtx = new PercolatorDbContext(options);
        schemaCtx.Database.EnsureCreated();

        // Return context without active identity (query filters removed)
        return new PercolatorDbContext(options);
    }
}
