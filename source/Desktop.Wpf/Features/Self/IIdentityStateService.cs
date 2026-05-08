using Percolator.Identity;
using R3;

namespace Desktop.Wpf.Features.Self;

public interface IIdentityStateService
{
    SelfId? Id { get; }
    ReadOnlyReactiveProperty<string> DisplayName { get; }
    ReadOnlyReactiveProperty<bool> Active { get; }
}
