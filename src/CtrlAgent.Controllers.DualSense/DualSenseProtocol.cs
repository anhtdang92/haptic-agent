namespace CtrlAgent.Controllers.DualSense;

[Flags]
public enum DualSenseButtons : uint
{
    None = 0,
    Square = 1 << 0,
    Cross = 1 << 1,
    Circle = 1 << 2,
    Triangle = 1 << 3,
    DPadUp = 1 << 4,
    DPadRight = 1 << 5,
    DPadDown = 1 << 6,
    DPadLeft = 1 << 7,
    L1 = 1 << 8,
    R1 = 1 << 9,
    L2Button = 1 << 10,
    R2Button = 1 << 11,
    Create = 1 << 12,
    Options = 1 << 13,
    L3 = 1 << 14,
    R3 = 1 << 15,
    PlayStation = 1 << 16,
    TouchpadClick = 1 << 17,
    Mute = 1 << 18,

    // DualSense Edge only (regular pads never set these bits).
    LeftFunction = 1 << 19,
    RightFunction = 1 << 20,
    LeftPaddle = 1 << 21,
    RightPaddle = 1 << 22,
}

/// <summary>One decoded input snapshot. Raw axes are 0..255 as on the wire.</summary>
public readonly record struct DualSenseInputState(
    byte LeftStickX,
    byte LeftStickY,
    byte RightStickX,
    byte RightStickY,
    byte LeftTrigger,
    byte RightTrigger,
    DualSenseButtons Buttons);

/// <summary>
/// DualSense wire protocol per the community-documented format (DS4Windows,
/// pydualsense lineage): input report 0x01 over USB, 0x31 over Bluetooth
/// (payload shifted by one), output report 0x02 (USB) / 0x31 + CRC32
/// (Bluetooth). Values are untested against real hardware until the first
/// validation pass — keep this class pure so tests pin the byte layout.
/// </summary>
public static class DualSenseProtocol
{
    public const ushort SonyVendorId = 0x054C;
    public const ushort DualSenseProductId = 0x0CE6;
    public const ushort DualSenseEdgeProductId = 0x0DF2;

    public const byte UsbInputReportId = 0x01;
    public const byte BluetoothInputReportId = 0x31;
    public const int UsbOutputReportLength = 48;
    public const int BluetoothOutputReportLength = 78;

    public static bool IsSupported(ushort vendorId, ushort productId) =>
        vendorId == SonyVendorId &&
        productId is DualSenseProductId or DualSenseEdgeProductId;

    public static bool TryParseInput(ReadOnlySpan<byte> report, out DualSenseInputState state)
    {
        state = default;

        int offset;
        if (report.Length >= 11 && report[0] == UsbInputReportId)
        {
            offset = 1;
        }
        else if (report.Length >= 12 && report[0] == BluetoothInputReportId)
        {
            offset = 2;
        }
        else
        {
            return false;
        }

        var buttons = DualSenseButtons.None;

        var buttons0 = report[offset + 7];
        buttons |= (buttons0 & 0x10) != 0 ? DualSenseButtons.Square : 0;
        buttons |= (buttons0 & 0x20) != 0 ? DualSenseButtons.Cross : 0;
        buttons |= (buttons0 & 0x40) != 0 ? DualSenseButtons.Circle : 0;
        buttons |= (buttons0 & 0x80) != 0 ? DualSenseButtons.Triangle : 0;
        buttons |= DecodeHat(buttons0 & 0x0F);

        var buttons1 = report[offset + 8];
        buttons |= (buttons1 & 0x01) != 0 ? DualSenseButtons.L1 : 0;
        buttons |= (buttons1 & 0x02) != 0 ? DualSenseButtons.R1 : 0;
        buttons |= (buttons1 & 0x04) != 0 ? DualSenseButtons.L2Button : 0;
        buttons |= (buttons1 & 0x08) != 0 ? DualSenseButtons.R2Button : 0;
        buttons |= (buttons1 & 0x10) != 0 ? DualSenseButtons.Create : 0;
        buttons |= (buttons1 & 0x20) != 0 ? DualSenseButtons.Options : 0;
        buttons |= (buttons1 & 0x40) != 0 ? DualSenseButtons.L3 : 0;
        buttons |= (buttons1 & 0x80) != 0 ? DualSenseButtons.R3 : 0;

        var buttons2 = report[offset + 9];
        buttons |= (buttons2 & 0x01) != 0 ? DualSenseButtons.PlayStation : 0;
        buttons |= (buttons2 & 0x02) != 0 ? DualSenseButtons.TouchpadClick : 0;
        buttons |= (buttons2 & 0x04) != 0 ? DualSenseButtons.Mute : 0;
        buttons |= (buttons2 & 0x10) != 0 ? DualSenseButtons.LeftFunction : 0;
        buttons |= (buttons2 & 0x20) != 0 ? DualSenseButtons.RightFunction : 0;
        buttons |= (buttons2 & 0x40) != 0 ? DualSenseButtons.LeftPaddle : 0;
        buttons |= (buttons2 & 0x80) != 0 ? DualSenseButtons.RightPaddle : 0;

        state = new DualSenseInputState(
            report[offset],
            report[offset + 1],
            report[offset + 2],
            report[offset + 3],
            report[offset + 4],
            report[offset + 5],
            buttons);
        return true;
    }

    /// <summary>Normalizes a raw stick byte to -1..1 (up/right positive for Y/X callers handle inversion).</summary>
    public static float NormalizeStick(byte raw) => Math.Clamp((raw - 127.5f) / 127.5f, -1f, 1f);

    public static float NormalizeTrigger(byte raw) => raw / 255f;

    /// <summary>
    /// USB output report: rumble motors plus lightbar and player-LED state.
    /// lowFrequency drives the left (heavy) motor, highFrequency the right
    /// (light) motor. The DualSense has no trigger rumble motors.
    /// </summary>
    public static byte[] BuildUsbOutput(
        float lowFrequency,
        float highFrequency,
        byte lightbarRed,
        byte lightbarGreen,
        byte lightbarBlue)
    {
        var report = new byte[UsbOutputReportLength];
        report[0] = 0x02;
        WritePayload(report.AsSpan(1), lowFrequency, highFrequency, lightbarRed, lightbarGreen, lightbarBlue);
        return report;
    }

    /// <summary>
    /// Bluetooth output report 0x31: sequence tag, the USB payload, and a
    /// CRC32 over 0xA2 plus the first 74 bytes appended little-endian.
    /// </summary>
    public static byte[] BuildBluetoothOutput(
        byte sequenceNumber,
        float lowFrequency,
        float highFrequency,
        byte lightbarRed,
        byte lightbarGreen,
        byte lightbarBlue)
    {
        var report = new byte[BluetoothOutputReportLength];
        report[0] = 0x31;
        report[1] = (byte)((sequenceNumber & 0x0F) << 4);
        report[2] = 0x10;
        WritePayload(report.AsSpan(3), lowFrequency, highFrequency, lightbarRed, lightbarGreen, lightbarBlue);

        var crc = ComputeOutputCrc(report.AsSpan(0, BluetoothOutputReportLength - 4));
        report[74] = (byte)(crc & 0xFF);
        report[75] = (byte)((crc >> 8) & 0xFF);
        report[76] = (byte)((crc >> 16) & 0xFF);
        report[77] = (byte)((crc >> 24) & 0xFF);
        return report;
    }

    /// <summary>CRC32 (reflected, poly 0xEDB88320) over the 0xA2 salt then the report prefix.</summary>
    public static uint ComputeOutputCrc(ReadOnlySpan<byte> reportPrefix)
    {
        var crc = 0xFFFFFFFFu;
        crc = Step(crc, 0xA2);
        foreach (var value in reportPrefix)
        {
            crc = Step(crc, value);
        }

        return ~crc;

        static uint Step(uint crc, byte value)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }

            return crc;
        }
    }

    private static void WritePayload(
        Span<byte> payload,
        float lowFrequency,
        float highFrequency,
        byte lightbarRed,
        byte lightbarGreen,
        byte lightbarBlue)
    {
        // valid_flag0: compatible rumble + haptics select; valid_flag1:
        // lightbar + player-indicator control.
        payload[0] = 0x03;
        payload[1] = 0x14;
        payload[2] = ToMotor(highFrequency);
        payload[3] = ToMotor(lowFrequency);
        payload[43] = 0x04; // center player LED
        payload[44] = lightbarRed;
        payload[45] = lightbarGreen;
        payload[46] = lightbarBlue;
    }

    private static byte ToMotor(float value) =>
        (byte)Math.Round(Math.Clamp(value, 0f, 1f) * 255f);

    private static DualSenseButtons DecodeHat(int hat) => hat switch
    {
        0 => DualSenseButtons.DPadUp,
        1 => DualSenseButtons.DPadUp | DualSenseButtons.DPadRight,
        2 => DualSenseButtons.DPadRight,
        3 => DualSenseButtons.DPadDown | DualSenseButtons.DPadRight,
        4 => DualSenseButtons.DPadDown,
        5 => DualSenseButtons.DPadDown | DualSenseButtons.DPadLeft,
        6 => DualSenseButtons.DPadLeft,
        7 => DualSenseButtons.DPadUp | DualSenseButtons.DPadLeft,
        _ => DualSenseButtons.None,
    };
}
