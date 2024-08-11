using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Desktop.Data;

public class Self
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int? Id { get; set; }
    [MaxLength(100)]
    public string IdentitySuffix { get; set; } = string.Empty;
    [MaxLength(100)]
    public string? PreferredNickname { get; set; }

    public bool BroadcastListen { get; set; } = false;
    public bool BroadcastSelf { get; set; } = false;
    public bool IntroduceListen { get; set; } = false;
    public bool AutoReplyIntroductions { get; set; } = false;
}