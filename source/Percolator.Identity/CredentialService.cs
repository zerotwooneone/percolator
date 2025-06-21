using System.Security.Cryptography;
using System.Text;

namespace Percolator.Identity
{
    public interface ICredentialService
    {
        string GetOrCreatePfxPassword();
    }

    public class CredentialService : ICredentialService
    {
        private readonly string _credentialFilePath;
        private const int PasswordLength = 32; // 32 chars for a strong password

        public CredentialService()
        {
            var appDataPath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var percolatorAppDataPath = Path.Combine(appDataPath, "Percolator");
            Directory.CreateDirectory(percolatorAppDataPath);
            _credentialFilePath = Path.Combine(percolatorAppDataPath, "pfx.cred");
        }

        internal CredentialService(string credentialFilePath)
        {
            _credentialFilePath = credentialFilePath;
            var directoryPath = Path.GetDirectoryName(_credentialFilePath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }
        }

        public string GetOrCreatePfxPassword()
        {
            if (File.Exists(_credentialFilePath))
            {
                var encryptedPasswordBytes = File.ReadAllBytes(_credentialFilePath);
                var passwordBytes = ProtectedData.Unprotect(encryptedPasswordBytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(passwordBytes);
            }
            else
            {
                var newPassword = GenerateRandomPassword();
                var passwordBytes = Encoding.UTF8.GetBytes(newPassword);
                var encryptedPasswordBytes = ProtectedData.Protect(passwordBytes, null, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_credentialFilePath, encryptedPasswordBytes);
                return newPassword;
            }
        }

        private static string GenerateRandomPassword()
        {
            const string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890!@#$%^&*()-_=+";
            var randomBytes = new byte[PasswordLength];
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(randomBytes);
            }

            var password = new StringBuilder(PasswordLength);
            foreach (var b in randomBytes)
            {
                password.Append(validChars[b % validChars.Length]);
            }
            return password.ToString();
        }
    }
}
