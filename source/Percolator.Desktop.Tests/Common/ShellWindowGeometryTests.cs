using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Percolator.Application.Ports;
using Percolator.Desktop.Common.Windowing;
using PublicIdentityId = Percolator.Domain.Identities.PublicIdentityId;

namespace Percolator.Desktop.Tests.Common;

/// <summary>
/// WIN-02 pure-math + store tests: visible-monitor clamping rules, minimized-skip capture,
/// corrupt/absent JSON handling — all verifiable without an Avalonia window.
/// </summary>
[TestFixture]
public sealed class ShellWindowGeometryTests
{
    // Primary working area: 1920×1080 at origin (taskbar already excluded).
    private static readonly Rect Primary = new(0, 0, 1920, 1080);

    // Secondary monitor to the right of the primary.
    private static readonly Rect Secondary = new(1920, 0, 1920, 1080);

    // ---- ClampToWorkingArea ----

    [Test]
    public void Clamp_FullyOnScreen_KeepsRect()
    {
        var candidate = new Rect(100, 100, 1200, 800);

        var result = ShellWindowGeometryMath.ClampToWorkingArea(
            candidate, new List<Rect> { Primary });

        result.Should().Be(candidate);
    }

    [Test]
    public void Clamp_OverHalfOnScreen_KeepsRect()
    {
        // 700 of 1200px on the primary (~58% of area) → keep.
        var candidate = new Rect(-500, 100, 1200, 800);

        var result = ShellWindowGeometryMath.ClampToWorkingArea(
            candidate, new List<Rect> { Primary });

        result.Should().Be(candidate);
    }

    [Test]
    public void Clamp_PartialOverlapUnderHalf_ShiftsOntoScreen()
    {
        // Only 100px on the primary — shift fully inside, preserving size.
        var candidate = new Rect(-1100, 100, 1200, 800);

        var result = ShellWindowGeometryMath.ClampToWorkingArea(
            candidate, new List<Rect> { Primary });

        result.X.Should().Be(0);
        result.Y.Should().Be(100);
        result.Width.Should().Be(1200);
        result.Height.Should().Be(800);
    }

    [Test]
    public void Clamp_PartialOverlapOnSecondary_ShiftsOntoThatScreen()
    {
        // Rect mostly off the right edge of the secondary monitor.
        var candidate = new Rect(3600, 100, 1200, 800);

        var result = ShellWindowGeometryMath.ClampToWorkingArea(
            candidate, new List<Rect> { Primary, Secondary });

        // 240px intersects the secondary → shift fully onto it.
        result.X.Should().Be(Secondary.Right - 1200);
        result.Y.Should().Be(100);
        result.Width.Should().Be(1200);
        result.Height.Should().Be(800);
    }

    [Test]
    public void Clamp_NoOverlap_RecentersOnPrimary()
    {
        // Saved on a monitor that no longer exists (e.g. was at x=5000).
        var candidate = new Rect(5000, 5000, 1200, 800);

        var result = ShellWindowGeometryMath.ClampToWorkingArea(
            candidate, new List<Rect> { Primary });

        result.X.Should().Be((1920 - 1200) / 2);
        result.Y.Should().Be((1080 - 800) / 2);
        result.Width.Should().Be(1200);
        result.Height.Should().Be(800);
    }

    [Test]
    public void Clamp_NoOverlapSizeExceedsScreen_ClampsToWorkingArea()
    {
        var candidate = new Rect(5000, 5000, 3000, 2000);

        var result = ShellWindowGeometryMath.ClampToWorkingArea(
            candidate, new List<Rect> { Primary });

        result.X.Should().Be(0);
        result.Y.Should().Be(0);
        result.Width.Should().Be(1920);
        result.Height.Should().Be(1080);
    }

    [Test]
    public void Clamp_EmptyScreenList_ReturnsCandidate()
    {
        var candidate = new Rect(50, 50, 1200, 800);

        ShellWindowGeometryMath
            .ClampToWorkingArea(candidate, new List<Rect>())
            .Should().Be(candidate);
    }

    // ---- TryCapture ----

    [Test]
    public void Capture_Normal_RecordsBoundsAndState()
    {
        var accepted = ShellWindowGeometryMath.TryCapture(
            null, WindowState.Normal, new Rect(10, 20, 1200, 800), out var result);

        accepted.Should().BeTrue();
        result.Should().Be(new ShellWindowGeometry(10, 20, 1200, 800, WindowState.Normal));
    }

    [Test]
    public void Capture_Minimized_Skips()
    {
        var accepted = ShellWindowGeometryMath.TryCapture(
            new ShellWindowGeometry(10, 20, 1200, 800, WindowState.Normal),
            WindowState.Minimized,
            new Rect(0, 0, 1, 1),
            out var result);

        accepted.Should().BeFalse("a minimized window's position is stale — nothing may persist");
        result.Should().BeNull();
    }

    [Test]
    public void Capture_Maximized_KeepsRestoreBounds_UpdatesState()
    {
        var restore = new ShellWindowGeometry(10, 20, 1200, 800, WindowState.Normal);

        var accepted = ShellWindowGeometryMath.TryCapture(
            restore,
            WindowState.Maximized,
            new Rect(0, 0, 1920, 1040), // maximized rect — must NOT be recorded
            out var result);

        accepted.Should().BeTrue();
        result.Should().Be(restore with { State = WindowState.Maximized },
            "maximized capture records the Normal restore rect, not the maximized bounds");
    }

    // ---- Store (JSON over app_settings) ----

    [Test]
    public async Task Store_SaveThenLoad_RoundTrips()
    {
        var store = new VaultShellWindowGeometryStore(
            new FakeVaultSettingsStore(), NullLogger<VaultShellWindowGeometryStore>.Instance);
        var geometry = new ShellWindowGeometry(120.5, 40, 1200, 800, WindowState.Maximized);

        await store.SaveAsync(geometry);
        var loaded = await store.LoadAsync();

        loaded.Should().Be(geometry);
    }

    [Test]
    public async Task Store_NoRecord_ReturnsNull()
    {
        var store = new VaultShellWindowGeometryStore(
            new FakeVaultSettingsStore(), NullLogger<VaultShellWindowGeometryStore>.Instance);

        (await store.LoadAsync()).Should().BeNull();
    }

    [Test]
    public async Task Store_CorruptJson_ReturnsNull()
    {
        var fake = new FakeVaultSettingsStore();
        await fake.SetAppSettingAsync(VaultSettingKeys.ShellWindowGeometry, "{not valid json!!");
        var store = new VaultShellWindowGeometryStore(
            fake, NullLogger<VaultShellWindowGeometryStore>.Instance);

        (await store.LoadAsync()).Should().BeNull("corrupt JSON is treated as absent");
    }

    [Test]
    public async Task Store_EmptyObject_ReturnsNull()
    {
        var fake = new FakeVaultSettingsStore();
        await fake.SetAppSettingAsync(VaultSettingKeys.ShellWindowGeometry, "{}");
        var store = new VaultShellWindowGeometryStore(
            fake, NullLogger<VaultShellWindowGeometryStore>.Instance);

        (await store.LoadAsync()).Should().BeNull("a zeroed record has no usable geometry");
    }

    /// <summary>Minimal in-memory <see cref="IVaultSettingsStore"/> — app-settings only.</summary>
    private sealed class FakeVaultSettingsStore : IVaultSettingsStore
    {
        private readonly Dictionary<string, string> _appSettings = new();

        public Task<IdentityLaunchSettings?> GetAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            Task.FromResult<IdentityLaunchSettings?>(null);

        public Task<IReadOnlyList<(PublicIdentityId IdentityId, IdentityLaunchSettings Settings)>> GetAllAsync(
            CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(PublicIdentityId, IdentityLaunchSettings)>>(
                Array.Empty<(PublicIdentityId, IdentityLaunchSettings)>());

        public Task SaveAsync(PublicIdentityId identityId, IdentityLaunchSettings settings, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(PublicIdentityId identityId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetAppSettingAsync(string key, CancellationToken ct = default) =>
            Task.FromResult(_appSettings.TryGetValue(key, out var v) ? v : null);

        public Task SetAppSettingAsync(string key, string value, CancellationToken ct = default)
        {
            _appSettings[key] = value;
            return Task.CompletedTask;
        }
    }
}
