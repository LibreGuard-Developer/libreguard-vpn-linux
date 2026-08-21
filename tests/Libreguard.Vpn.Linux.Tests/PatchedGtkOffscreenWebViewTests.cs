using Avalonia.Controls.Gtk;
using Avalonia.Input;

namespace Libreguard.Vpn.Linux.Tests;

public sealed class PatchedGtkOffscreenWebViewTests
{
    [Theory]
    [InlineData(PhysicalKey.Digit0, 0x13)]
    [InlineData(PhysicalKey.Digit1, 0x0A)]
    [InlineData(PhysicalKey.Digit9, 0x12)]
    [InlineData(PhysicalKey.NumPad0, 0x5A)]
    [InlineData(PhysicalKey.NumPad1, 0x57)]
    [InlineData(PhysicalKey.NumPad9, 0x51)]
    [InlineData(PhysicalKey.A, 0x26)]
    [InlineData(PhysicalKey.Z, 0x34)]
    [InlineData(PhysicalKey.Backspace, 0x16)]
    [InlineData(PhysicalKey.ArrowLeft, 0x71)]
    [InlineData(PhysicalKey.Tab, 0x17)]
    public void PhysicalKeys_MapToGtkHardwareCodes(PhysicalKey key, ushort expected)
        => Assert.Equal(expected, KeyTransform.ScanCodeFromPhysicalKey(key));

    [Fact]
    public void ShiftedTopRowSymbol_UsesTheActualShiftModifier()
    {
        var state = GtkOffscreenWebViewAdapter.ComposeKeyboardState(
            GdkModifierType.GDK_NO_MODIFIER_MASK,
            KeyModifiers.Shift);

        Assert.NotEqual((ushort)0, KeyTransform.ScanCodeFromPhysicalKey(PhysicalKey.Digit1));
        Assert.Equal(GdkModifierType.GDK_SHIFT_MASK, state);
    }

    [Fact]
    public void CapsAndNumLock_ArePreservedWithoutLeakingLiveShift()
    {
        var liveState = GdkModifierType.GDK_LOCK_MASK |
                        GdkModifierType.GDK_MOD2_MASK |
                        GdkModifierType.GDK_SHIFT_MASK;

        var state = GtkOffscreenWebViewAdapter.ComposeKeyboardState(liveState, KeyModifiers.None);

        Assert.True(state.HasFlag(GdkModifierType.GDK_LOCK_MASK));
        Assert.True(state.HasFlag(GdkModifierType.GDK_MOD2_MASK));
        Assert.False(state.HasFlag(GdkModifierType.GDK_SHIFT_MASK));
    }

    [Fact]
    public void FailedGtkSubmission_IsNotReportedHandled()
        => Assert.False(GtkOffscreenWebViewAdapter.SubmitKeyEvent(
            new IntPtr(1),
            new IntPtr(2),
            (_, _) => false));

    [Fact]
    public void SuccessfulGtkSubmission_IsReportedHandled()
        => Assert.True(GtkOffscreenWebViewAdapter.SubmitKeyEvent(
            new IntPtr(1),
            new IntPtr(2),
            (_, _) => true));

    [Fact]
    public void MissingGtkEvent_IsNotSubmittedOrReportedHandled()
    {
        var submitted = false;

        var handled = GtkOffscreenWebViewAdapter.SubmitKeyEvent(
            new IntPtr(1),
            IntPtr.Zero,
            (_, _) => submitted = true);

        Assert.False(handled);
        Assert.False(submitted);
    }

    [Theory]
    [InlineData(new byte[] { 255, 0, 0, 255 }, new byte[] { 0, 0, 255, 255 })]
    [InlineData(new byte[] { 0, 0, 255, 255 }, new byte[] { 255, 0, 0, 255 })]
    [InlineData(new byte[] { 100, 50, 200, 128 }, new byte[] { 100, 25, 50, 128 })]
    [InlineData(new byte[] { 255, 255, 255, 0 }, new byte[] { 0, 0, 0, 0 })]
    public void PixelConversion_SwapsRedBlueAndPremultipliesAlpha(byte[] rgba, byte[] expectedBgra)
    {
        var destination = new byte[4];

        GtkOffscreenPixelConverter.ConvertRgbaToPremultipliedBgra(
            rgba, 4, destination, 4, 1, 1);

        Assert.Equal(expectedBgra, destination);
    }

    [Fact]
    public void PixelConversion_HonorsUnequalStridesAndLeavesPaddingAlone()
    {
        var source = new byte[]
        {
            255, 0, 0, 255, 0, 0, 255, 255, 91, 92, 93, 94,
            0, 255, 0, 255, 20, 40, 60, 128, 81, 82, 83, 84
        };
        var destination = Enumerable.Repeat((byte)0xCC, 32).ToArray();

        GtkOffscreenPixelConverter.ConvertRgbaToPremultipliedBgra(
            source, 12, destination, 16, 2, 2);

        Assert.Equal(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 }, destination[..8]);
        Assert.All(destination[8..16], value => Assert.Equal(0xCC, value));
        Assert.Equal(new byte[] { 0, 255, 0, 255, 30, 20, 10, 128 }, destination[16..24]);
        Assert.All(destination[24..], value => Assert.Equal(0xCC, value));
    }
}
