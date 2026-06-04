using R3;

namespace Desktop.Wpf.Features.Self;

public interface IIdentityStateService
{
    ReadOnlyReactiveProperty<SelfIdentityModel> ActiveIdentity { get; }
}
