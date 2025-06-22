using System.Security.Cryptography;
using System.Text;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Percolator.Identity
{
    public interface ICredentialService
    {
        string GetOrCreatePfxPassword();
        byte[] Protect(byte[] data);
        byte[] Unprotect(byte[] data);
    }

    public class CredentialService : ICredentialService
    {
        private readonly string _credentialFilePath;
        private const int PasswordLength = 32; // 32 chars for a strong password

        // Using a static, hard-coded entropy value adds another layer of protection.
        // An attacker would need to compromise the user's account AND know this value
        // to decrypt the credential file.
        private static readonly byte[] Entropy = { 0x18, 0x27, 0x55, 0x9A, 0xBC, 0xDE, 0xF1, 0x23, 0x45, 0x67, 0x89, 0xAB, 0xCD, 0xEF, 0x12, 0x34 };

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
                var passwordBytes = ProtectedData.Unprotect(encryptedPasswordBytes, Entropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(passwordBytes);
            }
            else
            {
                var newPassword = GenerateRandomPassword();
                var passwordBytes = Encoding.UTF8.GetBytes(newPassword);
                var encryptedPasswordBytes = ProtectedData.Protect(passwordBytes, Entropy, DataProtectionScope.CurrentUser);
                File.WriteAllBytes(_credentialFilePath, encryptedPasswordBytes);
                SetFileSecurity(_credentialFilePath);
                return newPassword;
            }
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
                fileSecurity.AddAccessRule(rule);
                fileInfo.SetAccessControl(fileSecurity);
            }
        }
    }
}
