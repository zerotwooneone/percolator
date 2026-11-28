namespace Percolator.Infrastructure.Persistence;

using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Percolator.Domain.Common;
using Percolator.Domain.Security.Ports;
using Percolator.Infrastructure.Vault;

public sealed class SqliteDatabaseMigrator : IDatabaseMigrator
{
    private readonly PercolatorDbContext _dbContext;

    public SqliteDatabaseMigrator(PercolatorDbContext dbContext)
    {
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));
    }

    /// <inheritdoc />
    /// <remarks>
    /// <paramref name="masterKey"/> is unused: this scoped instance's DbContext is already
    /// key-pinned through the Stage-2 <see cref="SqlCipherPragmaInterceptor"/>.
    /// </remarks>
    public async Task<DomainResult> MigrateAsync(IMasterKeyHandle masterKey, CancellationToken ct = default)
    {
        try
        {
            var pending = _dbContext.Database.GetMigrations();
            if (pending.Any())
            {
                await _dbContext.Database.MigrateAsync(ct);
            }
            else
            {
                // EnsureCreated() cannot be used: it no-ops when ANY table exists, and the
                // vault always contains __percolator_vault written during initialization —
                // so schema would silently never apply. Generate the model DDL and apply it
                // idempotently via CREATE ... IF NOT EXISTS instead.
                var script = _dbContext.Database.GenerateCreateScript();
                var idempotent = Regex.Replace(
                    script,
                    @"CREATE (UNIQUE INDEX|INDEX|TABLE)\s",
                    "CREATE $1 IF NOT EXISTS ");
                await _dbContext.Database.ExecuteSqlRawAsync(idempotent, ct);
            }

            await ApplyPreKeySchemaUpgradesAsync(ct);
            await ApplyRetiredTableCleanupAsync(ct);

            return DomainResult.Success();
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError("DATABASE_MIGRATION_FAILED", ex.Message));
        }
    }

    private async Task ApplyRetiredTableCleanupAsync(CancellationToken ct)
    {
        await _dbContext.Database.ExecuteSqlRawAsync("""
            DROP TABLE IF EXISTS "relay_peer_directory";
            DROP TABLE IF EXISTS "relay_rendezvous_entries";
            DROP TABLE IF EXISTS "ft_swarm_endpoints";
            """, ct);
    }

    private async Task ApplyPreKeySchemaUpgradesAsync(CancellationToken ct)
    {
        var conn = _dbContext.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(ct);
        }

        await using var cmd = conn.CreateCommand();

        // 1. Check relay_prekeys SignedPreKeyId
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='relay_prekeys';";
        var relayTableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (relayTableCount > 0)
        {
            cmd.CommandText = "PRAGMA table_info('relay_prekeys');";
            var relayCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    relayCols.Add(reader.GetString(1));
                }
            }

            if (!relayCols.Contains("SignedPreKeyId"))
            {
                await _dbContext.Database.ExecuteSqlRawAsync(
                    "ALTER TABLE relay_prekeys ADD COLUMN SignedPreKeyId INTEGER NOT NULL DEFAULT 1;", ct);
            }
        }

        // 2. Check vault_signed_prekeys KeyId
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='vault_signed_prekeys';";
        var spkTableCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        if (spkTableCount > 0)
        {
            cmd.CommandText = "PRAGMA table_info('vault_signed_prekeys');";
            var spkCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    spkCols.Add(reader.GetString(1));
                }
            }

            if (!spkCols.Contains("KeyId"))
            {
                await _dbContext.Database.ExecuteSqlRawAsync("""
                    CREATE TABLE "vault_signed_prekeys_new" (
                        "OwnerIdentityId" TEXT NOT NULL,
                        "DeviceId" INTEGER NOT NULL,
                        "KeyId" INTEGER NOT NULL,
                        "PrivateKey" BLOB NOT NULL,
                        "PublicKey" BLOB NOT NULL,
                        "Signature" BLOB NOT NULL,
                        "IsCurrent" INTEGER NOT NULL,
                        "CreatedAtUtc" TEXT NOT NULL,
                        CONSTRAINT "PK_vault_signed_prekeys" PRIMARY KEY("OwnerIdentityId", "DeviceId", "KeyId"),
                        CONSTRAINT "FK_vault_signed_prekeys_vault_identity_keys_OwnerIdentityId"
                            FOREIGN KEY("OwnerIdentityId") REFERENCES "vault_identity_keys"("Id") ON DELETE CASCADE
                    );
                    INSERT INTO "vault_signed_prekeys_new" ("OwnerIdentityId","DeviceId","KeyId","PrivateKey","PublicKey","Signature","IsCurrent","CreatedAtUtc")
                        SELECT "OwnerIdentityId","DeviceId",1,"PrivateKey","PublicKey","Signature",1,"CreatedAtUtc" FROM "vault_signed_prekeys";
                    DROP TABLE "vault_signed_prekeys";
                    ALTER TABLE "vault_signed_prekeys_new" RENAME TO "vault_signed_prekeys";
                    """, ct);
            }
        }
    }
}
