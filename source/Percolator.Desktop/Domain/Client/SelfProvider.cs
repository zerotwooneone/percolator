using System.Security.Cryptography;
using Percolator.Desktop.Main;

namespace Percolator.Desktop.Domain.Client;

public class SelfProvider : ISelfProvider, ISelfInitializer
{
    private SelfModel? _self;
    
    public SelfModel GetSelf()
    {
        if (_self is null)
        {
            throw new InvalidOperationException("self has not been initialized");
        }
        return _self;
    }
    private const string KeyContainerName = "Percolator";
    public void InitSelf(Guid identitySuffix)
    {
        if (identitySuffix == Guid.Empty)
            throw new ArgumentException("identitySuffix cannot be null or empty", nameof(identitySuffix));
        var csp = new CspParameters
        {
            KeyContainerName = $"{KeyContainerName}.{identitySuffix}",
            Flags = 
                CspProviderFlags.UseArchivableKey | 
                CspProviderFlags.UseMachineKeyStore | 
                CspProviderFlags.UseDefaultKeyContainer
        };
        var identity = new RSACryptoServiceProvider(csp);
        _self = new SelfModel(identitySuffix, identity,GetRandomNickname(ByteUtils.GetIntFromBytes(identity.ExportRSAPublicKey())));
    }
    

    public static string GetRandomNickname(int? seed=null)
    {
        var random = seed is null 
            ? new Random() 
            : new Random(seed.Value);
        var numberOfChars = random.Next(9, 20);
        var vowels = new[] {'a', 'A', '4', '@', '^', 'e', 'E', '3', 'i', 'I', '1','o', 'O', '0', 'u', 'U', 'y', 'Y'};
        var consonants = new[]{'b', 'c', 'd', 'f', 'g', 'h', 'j', 'k', 'l', 'm', 'n', 'p', 'q', 'r', 's', 't', 'v', 'w', 'x', 'z','B', 'C','(', 'D', 'F', 'G', 'H','#','8', 'J', 'K', 'L','7', 'M', 'N', 'P', 'Q', 'R', 'S','$', 'T', 'V', 'W', 'X', 'Z'};
        var list = new List<char>(numberOfChars*2);
        var isVowel = random.Next(1,1001) %2 == 0;
        do
        {
            var newPart = isVowel
                ? Enumerable.Range(0, random.Next(1, 3)).Select(_ => vowels[random.Next(0, vowels.Length)])
                : new[] {consonants[random.Next(0, consonants.Length)]};
            list.AddRange(newPart);
            isVowel = !isVowel;
        } while (list.Count < numberOfChars);

        return new string(list.ToArray());
    }
}