using System;
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
        var identity = new IdentityRecord(idGuid, display)
        {
            SelfIdentityId = new SelfId(selfIdentityId)
        };
        active.SetActiveIdentity(identity, null);
        return active;
    }

    public static PercolatorDbContext NewContext(DbContextOptions<PercolatorDbContext> options, int? selfIdentityId = 1)
    {
        if (selfIdentityId is null)
        {
            return new PercolatorDbContext(options);
        }
        var active = CreateActiveIdentity(selfIdentityId.Value);
        return new PercolatorDbContext(options, active);
    }
}
