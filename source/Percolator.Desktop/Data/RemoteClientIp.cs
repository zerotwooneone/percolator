using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Percolator.Desktop.Data;

public class RemoteClientIp
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int? Id { get; set; }
    [MaxLength(100)]
    public string IpAddress { get; set; }
    public int RemoteClientId { get; set; }
    public RemoteClient RemoteClient { get; set; }
}