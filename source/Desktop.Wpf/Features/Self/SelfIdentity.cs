using R3;

namespace Desktop.Wpf.Features.Self;

public sealed class SelfIdentity
{
    public BindableReactiveProperty<string> DisplayName { get; } = new("Operator");
    public BindableReactiveProperty<string> Initials { get; } = new("OP");
    public BindableReactiveProperty<string> Id { get; } = new("8X92-A4");
}
