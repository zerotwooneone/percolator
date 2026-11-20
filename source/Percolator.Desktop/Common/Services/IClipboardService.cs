using System.Threading.Tasks;

namespace Percolator.Desktop.Common.Services;

public interface IClipboardService
{
    Task<string?> GetTextAsync();
    Task SetTextAsync(string text);
}
