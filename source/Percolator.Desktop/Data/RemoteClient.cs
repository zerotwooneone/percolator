using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Diagnostics.CodeAnalysis;

namespace Percolator.Desktop.Data;

public class RemoteClient
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int? Id { get; set; }
    [MaxLength(200)]
    public string Identity { get; set; }
    
    [MaxLength(140)]
    public string? PreferredNickname { get; set; }
    public long LastSeenUtc { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    public ICollection<RemoteClientIp> RemoteClientIps { get; set; }=new List<RemoteClientIp>();

    public DateTimeOffset GetLocalLastSeen()
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(LastSeenUtc).ToLocalTime();
    }

    public void SetLastSeen(DateTimeOffset? lastSeen = null)
    {
        LastSeenUtc= lastSeen?.ToUniversalTime().ToUnixTimeMilliseconds() ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }
    public void SetIdentity(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            throw new ArgumentException(nameof(bytes));
        }
        Identity = Convert.ToBase64String(bytes);
    }

    public bool TryGetIdentityBytes([NotNullWhen(true)] out byte[]? bytes)
    {
        if (string.IsNullOrWhiteSpace(Identity))
        {
            bytes = null;
            return false;
        }

        try
        {
            bytes= Convert.FromBase64String(Identity);
            return true;
        }
        catch (FormatException)
        {
            bytes = null;
            return false;
        }
    }
}