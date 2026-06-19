using Percolator.Identity;
using R3;

namespace Desktop.Wpf.Features.Self;

public interface IIdentityStateService
{
    ReadOnlyReactiveProperty<SelfIdentityModel> ActiveIdentity { get; }
    void UpdateDisplayName(SelfId targetId, string newName);
}
