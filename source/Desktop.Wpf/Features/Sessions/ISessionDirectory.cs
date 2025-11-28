using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Wpf.Features.Sessions;

public interface ISessionDirectory
{
    Task<IReadOnlyList<SessionListItem>> GetAllAsync(CancellationToken ct);
}
