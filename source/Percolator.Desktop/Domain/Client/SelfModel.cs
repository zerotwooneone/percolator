using System.Security.Cryptography;
using R3;

namespace Percolator.Desktop.Domain.Client;

public class SelfModel 
{
    public Guid IdentitySuffix { get; }
    public RSA Identity { get; }
    public ReactiveProperty<string> PreferredNickname { get; }

    public SelfModel(Guid identitySuffix, RSA identity, string preferredNickname)
    {
        IdentitySuffix = identitySuffix;
        Identity = identity;
        PreferredNickname = new ReactiveProperty<string>(preferredNickname);
    }
}