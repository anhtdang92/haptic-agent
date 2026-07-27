using System.Runtime.InteropServices;

namespace CtrlAgent.Platform.Windows;

internal static partial class XInputNative
{
    internal const uint ErrorSuccess = 0;
    internal const uint ErrorDeviceNotConnected = 1167;

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    internal static partial uint GetState(uint userIndex, out XInputState state);

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    internal static partial uint SetState(uint userIndex, ref XInputVibration vibration);

    /// <summary>
    /// XInputGetStateEx — undocumented, exported by ordinal 100 only. It is
    /// byte-identical to XInputGetState except that it also reports the
    /// Guide button (<see cref="XInputButtons.Guide"/>), which the public
    /// entry point deliberately masks off.
    /// </summary>
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint userIndex, out XInputState state);

    // Resolved once: if the ordinal is missing we never probe again and every
    // caller silently falls back to the public entry point (no Guide button).
    private static bool _extendedProbed;
    private static bool _extendedAvailable;

    /// <summary>
    /// Reads controller state, preferring the extended entry point so the
    /// Guide button is visible. Falls back to <see cref="GetState"/> on any
    /// runtime that does not export ordinal 100.
    /// </summary>
    internal static uint GetStateWithGuide(uint userIndex, out XInputState state)
    {
        if (!_extendedProbed)
        {
            _extendedProbed = true;
            try
            {
                _ = XInputGetStateEx(0, out _);
                _extendedAvailable = true;
            }
            catch (EntryPointNotFoundException)
            {
                _extendedAvailable = false;
            }
            catch (DllNotFoundException)
            {
                _extendedAvailable = false;
            }
        }

        if (_extendedAvailable)
        {
            try
            {
                return XInputGetStateEx(userIndex, out state);
            }
            catch (EntryPointNotFoundException)
            {
                _extendedAvailable = false;
            }
        }

        return GetState(userIndex, out state);
    }

    /// <summary>
    /// Whether the Guide button can be observed on this runtime, i.e. whether
    /// ordinal 100 resolved. Probes once if nothing has read state yet, so a
    /// caller asking before the first poll still gets the real answer rather
    /// than a default.
    /// </summary>
    internal static bool SupportsGuideButton()
    {
        if (!_extendedProbed)
        {
            GetStateWithGuide(0, out _);
        }

        return _extendedAvailable;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputState
{
    public uint PacketNumber;
    public XInputGamepad Gamepad;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputGamepad
{
    public ushort Buttons;
    public byte LeftTrigger;
    public byte RightTrigger;
    public short ThumbLX;
    public short ThumbLY;
    public short ThumbRX;
    public short ThumbRY;
}

[StructLayout(LayoutKind.Sequential)]
internal struct XInputVibration
{
    public ushort LeftMotorSpeed;
    public ushort RightMotorSpeed;
}

[Flags]
internal enum XInputButtons : ushort
{
    DPadUp = 0x0001,
    DPadDown = 0x0002,
    DPadLeft = 0x0004,
    DPadRight = 0x0008,
    Menu = 0x0010,
    View = 0x0020,
    LeftThumb = 0x0040,
    RightThumb = 0x0080,
    LeftShoulder = 0x0100,
    RightShoulder = 0x0200,
    // Reported only through the ordinal-100 entry point.
    Guide = 0x0400,
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}
