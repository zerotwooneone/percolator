// See https://aka.ms/new-console-template for more information

using System.Security.Cryptography;
using System.Text;

const string KeyContainerName = "Percolator";

var csp = new CspParameters
{
    KeyContainerName = KeyContainerName,
    Flags = 
        CspProviderFlags.UseArchivableKey | 
        CspProviderFlags.UseMachineKeyStore | 
        CspProviderFlags.UseDefaultKeyContainer
};

byte[]? encrypted = null;
//this gets or creates the key
using (var rsa = new RSACryptoServiceProvider(csp))
{
    //Console.WriteLine($"rsa key: {rsa.ToXmlString(true)}");
    encrypted = rsa.Encrypt("Hello World!"u8.ToArray(), false);
}

// using (var deleteRsa = new RSACryptoServiceProvider(csp))
// {
//     deleteRsa.PersistKeyInCsp = false;
//     //this deletes the stored key, and disposes the object
//     deleteRsa.Clear();
//
//     Console.WriteLine("Deleted key");
// }

using (var decrypter = new RSACryptoServiceProvider(csp))
{
    var decrypted = decrypter.Decrypt(encrypted, false);
    Console.WriteLine(Encoding.UTF8.GetString(decrypted, 0, decrypted.Length));
}