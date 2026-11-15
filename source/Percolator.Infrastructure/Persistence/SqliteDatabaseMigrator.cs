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

            // DRAIN-10 schema upgrade — the CREATE-IF-NOT-EXISTS path never alters
            // pre-existing tables, so the two prekey changes need guarded rewrites:
            // a new column on relay_prekeys and a new composite PK on vault_signed_prekeys.
            await ApplyPreKeySchemaUpgradesAsync(ct);

            return DomainResult.Success();
        }
        catch (Exception ex)
        {
            return DomainResult.Failure(new DomainError("DATABASE_MIGRATION_FAILED", ex.Message));
        }
    }

    private async Task ApplyPreKeySchemaUpgradesAsync(CancellationToken ct)
    {
        // relay_prekeys gains the publisher-assigned signed-prekey id used by
        // GetPreKeyStatus/FetchPreKeyBundle reporting.
        var relaySpkColumn = await _dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info('relay_prekeys') WHERE name='SignedPreKeyId'")
            .FirstAsync(ct);
        if (relaySpkColumn == 0)
        {
            await _dbContext.Database.ExecuteSqlRawAsync(
                "ALTER TABLE relay_prekeys ADD COLUMN SignedPreKeyId INTEGER NOT NULL DEFAULT 1;", ct);
        }

        // vault_signed_prekeys keeps multiple SPK generations — the legacy
        // (OwnerIdentityId, DeviceId) PK permits only one row per device, so the
        // table is rebuilt with KeyId in the PK when the column is absent.
        var spkKeyColumn = await _dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM pragma_table_info('vault_signed_prekeys') WHERE name='KeyId'")
            .FirstAsync(ct);
        var spkTable = await _dbContext.Database
            .SqlQuery<int>($"SELECT COUNT(*) AS Value FROM sqlite_master WHERE type='table' AND name='vault_signed_prekeys'")
            .FirstAsync(ct);
        if (spkTable != 0 && spkKeyColumn == 0)
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
