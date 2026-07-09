using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Percolator.Infrastructure.Security
{
    public class DatabaseEncryptionService : IDatabaseEncryptionService
    {
        private readonly StorageOptions _storageOptions;
        private readonly string _keyFilePath;
        private string? _password;
        private readonly object _lock = new object();

        public DatabaseEncryptionService(IOptions<StorageOptions> storageOptions)
        {
            _storageOptions = storageOptions.Value;
            _keyFilePath = Path.Combine(_storageOptions.Path, "db.key");
        }

        public string GetDatabasePassword()
        {
            if (_password != null)
            {
                return _password;
            }

            lock (_lock)
            {
                if (_password != null)
                {
                    return _password;
                }

                if (File.Exists(_keyFilePath))
                {
                    var encryptedPassword = File.ReadAllBytes(_keyFilePath);
                    var passwordBytes = ProtectedData.Unprotect(encryptedPassword, null, DataProtectionScope.CurrentUser);
                    _password = Encoding.UTF8.GetString(passwordBytes);
                }
                else
                {
                    var passwordBytes = new byte[32];
                    using (var rng = RandomNumberGenerator.Create())
                    {
                        rng.GetBytes(passwordBytes);
                    }
                    _password = Convert.ToBase64String(passwordBytes);

                    var encryptedPassword = ProtectedData.Protect(Encoding.UTF8.GetBytes(_password), null, DataProtectionScope.CurrentUser);
                    File.WriteAllBytes(_keyFilePath, encryptedPassword);
                }

                return _password;
            }
        }
    }
}
