using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Percolator.Identity
{
    public interface ICredentialService
    {
        Task<string> GetOrCreateCredentialAsync(string identityName, string purpose);
        Task<string?> GetCredentialAsync(string identityName, string purpose);
        byte[] Protect(byte[] data);
        byte[] Unprotect(byte[] data);
    }

    public class CredentialService : ICredentialService
    {
        private const int PasswordLength = 32; // 32 chars for a strong password
        private static readonly byte[] Entropy = { 0x18, 0x27, 0x55, 0x9A, 0xBC, 0xDE, 0xF1, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x12, 0x34 };
        private readonly ILogger<CredentialService> _logger;

        public CredentialService(ILogger<CredentialService> logger)
        {
            _logger = logger;
        }

        public async Task<string> GetOrCreateCredentialAsync(string identityName, string purpose)
        {
            var credential = await GetCredentialAsync(identityName, purpose);
            if (credential is not null)
            {
                return credential;
            }

            _logger.LogInformation("No credential found for identity '{IdentityName}' and purpose '{Purpose}'. Creating a new one.", identityName, purpose);

            var newPassword = GenerateRandomPassword();
            var passwordBytes = Encoding.UTF8.GetBytes(newPassword);
            var encryptedPasswordBytes = Protect(passwordBytes);

            var credentialPath = GetCredentialPath(identityName, purpose);
            var directoryPath = Path.GetDirectoryName(credentialPath);
            if(directoryPath is not null)
            {
                Directory.CreateDirectory(directoryPath);
            }
            
            await File.WriteAllBytesAsync(credentialPath, encryptedPasswordBytes);
            SetFileSecurity(credentialPath);
            return newPassword;
        }

        public async Task<string?> GetCredentialAsync(string identityName, string purpose)
        {
            var credentialPath = GetCredentialPath(identityName, purpose);
            if (!File.Exists(credentialPath))
            {
                return null;
            }

            var encryptedPasswordBytes = await File.ReadAllBytesAsync(credentialPath);
            var passwordBytes = Unprotect(encryptedPasswordBytes);
            return Encoding.UTF8.GetString(passwordBytes);
        }
        
        private string GetCredentialPath(string identityName, string purpose)
        {
            var basePath = IdentityPathHelper.GetBasePath(identityName);
            var credentialsPath = Path.Combine(basePath, "creds");
            var sanitizedPurpose = string.Join("_", purpose.Split(Path.GetInvalidFileNameChars()));
            return Path.Combine(credentialsPath, $"{sanitizedPurpose}.cred");
        }

        public byte[] Protect(byte[] data)
        {
            return ProtectedData.Protect(data, Entropy, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] data)
        {
            return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
        }

        private static string GenerateRandomPassword()
        {
            const string validChars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890!@#$%^&*()-_=+";
            var password = new StringBuilder(PasswordLength);
            using var rng = RandomNumberGenerator.Create();

            // To avoid modulo bias, we'll only accept values within the largest multiple of validChars.Length that fits in a byte.
            var maxMultiple = (256 / validChars.Length) * validChars.Length;
            
            var randomBytes = new byte[1];
            while (password.Length < PasswordLength)
            {
                rng.GetBytes(randomBytes);
                var randomValue = randomBytes[0];

                if (randomValue < maxMultiple)
                {
                    password.Append(validChars[randomValue % validChars.Length]);
                }
            }
            return password.ToString();
        }

        private void SetFileSecurity(string filePath)
        {
            var fileInfo = new FileInfo(filePath);
            var fileSecurity = fileInfo.GetAccessControl();
            var currentUser = WindowsIdentity.GetCurrent().User;
            if (currentUser is not null)
            {
                fileSecurity.SetOwner(currentUser);
                var rule = new FileSystemAccessRule(
                    currentUser,
                    FileSystemRights.FullControl,
                    AccessControlType.Allow);
                fileSecurity.SetAccessRule(rule);
                fileInfo.SetAccessControl(fileSecurity);
            }
        }
    }
}
