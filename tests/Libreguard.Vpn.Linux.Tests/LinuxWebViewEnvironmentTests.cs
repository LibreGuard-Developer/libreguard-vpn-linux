using Avalonia.Input;
using Avalonia.Controls.Gtk;
using Libreguard.Vpn.Linux.Services;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class LinuxWebViewEnvironmentTests
{
    [Fact]
    public void SelectionCache_DefersNativeDetectionUntilFirstAccess()
    {
        var expected = LinuxWebViewEnvironment.SelectProfiles(Capabilities(), false, null);
        var factoryCalls = 0;
        var cache = LinuxWebViewEnvironment.CreateSelectionCache(() =>
        {
            factoryCalls++;
            return expected;
        });

        Assert.False(cache.IsValueCreated);
        Assert.Equal(0, factoryCalls);
        Assert.Same(expected, cache.Value);
        Assert.True(cache.IsValueCreated);
        Assert.Equal(1, factoryCalls);
        Assert.Same(expected, cache.Value);
        Assert.Equal(1, factoryCalls);
    }

    [Theory]
    [InlineData("wayland", ":0", null, true)]
    [InlineData("wayland", ":0", "wayland", true)]
    [InlineData("wayland", ":0", "x11", false)]
    [InlineData("x11", ":0", null, false)]
    [InlineData("wayland", null, "wayland", false)]
    public void GtkBackend_UsesX11OnlyForWaylandSessionsWithAnAvaloniaDisplay(
        string? sessionType,
        string? display,
        string? currentBackend,
        bool expected)
        => Assert.Equal(expected, LinuxWebViewEnvironment.ShouldForceX11GtkBackend(sessionType, display, currentBackend));

    [Fact]
    public void Auto_AcceleratedX11_PrefersNativeGtkThenFallbacks()
    {
        var selection = LinuxWebViewEnvironment.SelectProfiles(
            Capabilities(renderer: "Intel Iris accelerated", hardware: true),
            wpeAvailable: true,
            requestedMode: "auto");

        Assert.Equal(
            [
                CheckoutWebViewProfile.GtkNativeAccelerated,
                CheckoutWebViewProfile.WpeSharedMemory,
                CheckoutWebViewProfile.GtkOffscreenCompatibility,
                CheckoutWebViewProfile.Browser
            ],
            selection.Profiles);
    }

    [Theory]
    [InlineData("llvmpipe (LLVM 19)")]
    [InlineData("softpipe")]
    [InlineData("Software Rasterizer")]
    [InlineData("VirtualBox Graphics Adapter")]
    [InlineData("VMware SVGA II")]
    public void Auto_SoftwareOrUnacceleratedRenderer_DoesNotUseNativeGtk(string renderer)
    {
        var selection = LinuxWebViewEnvironment.SelectProfiles(
            Capabilities(renderer: renderer, hardware: false),
            wpeAvailable: true,
            requestedMode: null);

        Assert.Equal(CheckoutWebViewProfile.WpeSharedMemory, selection.Profiles[0]);
        Assert.DoesNotContain(CheckoutWebViewProfile.GtkNativeAccelerated, selection.Profiles);
    }

    [Fact]
    public void Auto_VirtualMachineWithWorkingAcceleration_IsNotBlacklisted()
    {
        var selection = LinuxWebViewEnvironment.SelectProfiles(
            Capabilities(renderer: "VirtualBox accelerated 3D", hardware: true),
            wpeAvailable: false,
            requestedMode: null);

        Assert.Equal(CheckoutWebViewProfile.GtkNativeAccelerated, selection.Profiles[0]);
    }

    [Theory]
    [InlineData("wayland", true, true, true)]
    [InlineData("x11", false, true, true)]
    [InlineData("x11", true, false, true)]
    [InlineData("x11", true, true, false)]
    public void Auto_UnsuitableNativeEnvironment_UsesCompatibilityWhenWpeMissing(
        string session,
        bool display,
        bool egl,
        bool renderNode)
    {
        var capabilities = Capabilities(session, display, egl, renderNode, "AMD Radeon accelerated", true);
        var selection = LinuxWebViewEnvironment.SelectProfiles(capabilities, false, null);

        Assert.Equal(CheckoutWebViewProfile.GtkOffscreenCompatibility, selection.Profiles[0]);
    }

    [Theory]
    [InlineData("gtk-native", "GtkNativeAccelerated")]
    [InlineData("wpe", "WpeSharedMemory")]
    [InlineData("gtk-offscreen", "GtkOffscreenCompatibility")]
    [InlineData("browser", "Browser")]
    public void ExplicitOverride_SelectsRequestedProfile(string mode, string expected)
    {
        var selection = LinuxWebViewEnvironment.SelectProfiles(
            Capabilities(renderer: "llvmpipe", hardware: false),
            false,
            mode);

        Assert.Equal(expected, selection.Profiles[0].ToString());
    }

    [Fact]
    public void CorrectedModifierState_PreservesLockStatesAndUsesActualEventModifiers()
    {
        var keymapState = GdkModifierType.GDK_LOCK_MASK |
                          GdkModifierType.GDK_MOD2_MASK |
                          GdkModifierType.GDK_SHIFT_MASK |
                          GdkModifierType.GDK_ALT_MASK;

        var state = GtkOffscreenWebViewAdapter.ComposeKeyboardState(keymapState, KeyModifiers.Control);

        Assert.True(state.HasFlag(GdkModifierType.GDK_LOCK_MASK));
        Assert.True(state.HasFlag(GdkModifierType.GDK_MOD2_MASK));
        Assert.True(state.HasFlag(GdkModifierType.GDK_CONTROL_MASK));
        Assert.False(state.HasFlag(GdkModifierType.GDK_SHIFT_MASK));
        Assert.False(state.HasFlag(GdkModifierType.GDK_ALT_MASK));
    }

    [Theory]
    [InlineData(PhysicalKey.Digit0)]
    [InlineData(PhysicalKey.Digit1)]
    [InlineData(PhysicalKey.Digit9)]
    [InlineData(PhysicalKey.NumPad0)]
    [InlineData(PhysicalKey.NumPad1)]
    [InlineData(PhysicalKey.NumPad9)]
    [InlineData(PhysicalKey.A)]
    [InlineData(PhysicalKey.Enter)]
    public void CompatibilityKeyboard_ResolvesTopRowNumpadAndGeneralKeys(PhysicalKey physicalKey)
    {
        Assert.NotEqual((ushort)0, KeyTransform.ScanCodeFromPhysicalKey(physicalKey));
    }

    private static LinuxGraphicsCapabilities Capabilities(
        string session = "x11",
        bool display = true,
        bool egl = true,
        bool renderNode = true,
        string renderer = "Intel accelerated",
        bool hardware = true)
        => new(session, display, egl, renderNode, renderer, hardware);
}
