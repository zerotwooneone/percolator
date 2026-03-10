using System;
using System.Threading;
using System.Windows;
using Desktop.Wpf.Features.Shell;
using Desktop.Wpf.Shared.Windowing;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;

namespace Desktop.Wpf.Tests;

[TestFixture]
[Apartment(ApartmentState.STA)]
public sealed class WindowManagerLifecycleTests
{
    private sealed class ScopeProbe : IDisposable
    {
        private readonly Action _onDispose;
        public ScopeProbe(Action onDispose) => _onDispose = onDispose;
        public void Dispose() => _onDispose();
    }

    private sealed class DummyVm { }

    private sealed class DummyWindow : Window
    {
        public DummyWindow(ScopeProbe probe)
        {
            Probe = probe;
            Width = 10;
            Height = 10;
            ShowInTaskbar = false;
            WindowStyle = WindowStyle.None;
        }

        public ScopeProbe Probe { get; }
    }

    [SetUp]
    public void SetUp() => WpfTestHarness.EnsureApplication();

    [Test]
    public void ShowFor_AllowsOpenCloseOpen_AndDisposesPerWindowScope()
    {
        // Arrange
        var disposedCount = 0;
        var services = new ServiceCollection();
        services.AddScoped(_ => new ScopeProbe(() => Interlocked.Increment(ref disposedCount)));
        services.AddScoped<DummyVm>();
        services.AddScoped<DummyWindow>();

        var provider = services.BuildServiceProvider();

        var identityScopeAccessor = new IdentityScopeAccessor { Current = provider };
        var registry = new WindowViewRegistry();
        registry.Register(typeof(DummyVm), typeof(DummyWindow));

        var sut = new WindowManager(identityScopeAccessor, registry);

        // Act
        sut.ShowFor<DummyVm>().Should().BeTrue();
        var first = (DummyWindow)Application.Current.Windows[Application.Current.Windows.Count - 1];
        first.Close();

        sut.ShowFor<DummyVm>().Should().BeTrue();
        var second = (DummyWindow)Application.Current.Windows[Application.Current.Windows.Count - 1];

        // Assert
        second.Should().NotBeSameAs(first);

        // Close second to ensure scope disposal is triggered for it as well.
        second.Close();

        disposedCount.Should().Be(2);
    }
}
