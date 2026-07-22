using System.Runtime.InteropServices;

namespace HapticAgent.Platform.Windows;

internal static partial class XInputNative
{
    internal const uint ErrorSuccess = 0;
    internal const uint ErrorDeviceNotConnected = 1167;

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    internal static partial uint GetState(uint userIndex, out XInputState state);

    [LibraryImport("xinput1_4.dll", EntryPoint = "XInputSetState")]
    internal static partial uint SetState(uint userIndex, ref XInputVibration vibration);
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
    A = 0x1000,
    B = 0x2000,
    X = 0x4000,
    Y = 0x8000,
}
