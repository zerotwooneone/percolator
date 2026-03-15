using System.Security.Cryptography;
using System.Text;
using Percolator.Application.Apps.Chat;

namespace Percolator.Infrastructure.Security
{
    // Uses the existing database encryption password as entropy to derive a 32-byte master key for at-rest state
    internal sealed class AtRestKeyProvider : IAtRestKeyProvider
    {
        private readonly IDatabaseEncryptionService _dbEnc;
        public AtRestKeyProvider(IDatabaseEncryptionService dbEnc)
        {
            _dbEnc = dbEnc;
        }

        public Task<byte[]> GetMasterKeyAsync(CancellationToken ct)
        {
            var pwd = _dbEnc.GetDatabasePassword();
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(pwd));
            return Task.FromResult(bytes);
        }
    }
}
