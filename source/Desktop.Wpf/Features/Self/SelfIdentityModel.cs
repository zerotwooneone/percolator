using Percolator.Identity;
using Percolator.Identity.Model;
using R3;

namespace Desktop.Wpf.Features.Self;

public sealed class SelfIdentityModel(SelfId id, string displayName, string initials, ListeningPort listeningPort, bool active=false)
{
    public ReactiveProperty<string> DisplayName { get; } = new(displayName);
    public ReadOnlyReactiveProperty<string> Initials { get; } = new ReactiveProperty<string>(initials);
    public SelfId Id { get; } = id;
    public ReadOnlyReactiveProperty<ListeningPort> ListeningPort { get; } = new ReactiveProperty<ListeningPort>(listeningPort);
    public ReactiveProperty<bool> Active { get; } = new(active);
}
