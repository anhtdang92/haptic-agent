using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CtrlAgent.Platform.Windows;

/// <summary>
/// Minimal Windows HID plumbing for the DualSense: enumerate HID interfaces,
/// match VID/PID, open with overlapped I/O, and read report lengths.
/// </summary>
internal static class DualSenseHidNative
{
    private const int DigcfPresent = 0x2;
    private const int DigcfDeviceInterface = 0x10;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int HidpStatusSuccess = 0x110000;

    internal sealed record HidDeviceInfo(
        string Path,
        ushort VendorId,
        ushort ProductId,
        ushort UsagePage,
        ushort Usage,
        ushort InputReportLength,
        ushort OutputReportLength,
        ushort FeatureReportLength);

    /// <summary>Enumerates present HID interfaces with their identity and caps.</summary>
    public static IEnumerable<HidDeviceInfo> EnumerateDevices()
    {
        HidD_GetHidGuid(out var hidGuid);
        var deviceSet = SetupDiGetClassDevsW(ref hidGuid, null, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
        if (deviceSet == IntPtr.Zero || deviceSet == new IntPtr(-1))
        {
            yield break;
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var interfaceData = new SpDeviceInterfaceData
                {
                    CbSize = Marshal.SizeOf<SpDeviceInterfaceData>(),
                };

                if (!SetupDiEnumDeviceInterfaces(deviceSet, IntPtr.Zero, ref hidGuid, index, ref interfaceData))
                {
                    yield break;
                }

                var path = GetInterfacePath(deviceSet, ref interfaceData);
                if (path is null)
                {
                    continue;
                }

                var info = ProbeDevice(path);
                if (info is not null)
                {
                    yield return info;
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceSet);
        }
    }

    public static SafeFileHandle OpenDevice(string path)
    {
        var handle = CreateFileW(
            path,
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOverlapped,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new IOException($"Could not open HID device: {Marshal.GetLastWin32Error()}");
        }

        return handle;
    }

    /// <summary>Best-effort feature-report read (switches Bluetooth pads to full reports).</summary>
    public static void TryGetFeature(SafeFileHandle handle, byte reportId, int length)
    {
        var buffer = new byte[Math.Max(length, 2)];
        buffer[0] = reportId;
        _ = HidD_GetFeature(handle, buffer, buffer.Length);
    }

    private static string? GetInterfacePath(IntPtr deviceSet, ref SpDeviceInterfaceData interfaceData)
    {
        SetupDiGetDeviceInterfaceDetailW(deviceSet, ref interfaceData, IntPtr.Zero, 0, out var requiredSize, IntPtr.Zero);
        if (requiredSize <= 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal(requiredSize);
        try
        {
            // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA: 8 on x64, 6 on x86.
            Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetailW(deviceSet, ref interfaceData, buffer, requiredSize, out _, IntPtr.Zero))
            {
                return null;
            }

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static HidDeviceInfo? ProbeDevice(string path)
    {
        using var handle = CreateFileW(
            path,
            0,
            FileShareRead | FileShareWrite,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        var attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
        if (!HidD_GetAttributes(handle, ref attributes))
        {
            return null;
        }

        if (!HidD_GetPreparsedData(handle, out var preparsed))
        {
            return null;
        }

        try
        {
            if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess)
            {
                return null;
            }

            return new HidDeviceInfo(
                path,
                attributes.VendorId,
                attributes.ProductId,
                caps.UsagePage,
                caps.Usage,
                caps.InputReportByteLength,
                caps.OutputReportByteLength,
                caps.FeatureReportByteLength);
        }
        finally
        {
            HidD_FreePreparsedData(preparsed);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public int CbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HiddAttributes
    {
        public int Size;
        public ushort VendorId;
        public ushort ProductId;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HidpCaps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetAttributes(SafeFileHandle hidDeviceObject, ref HiddAttributes attributes);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, out HidpCaps capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(
        IntPtr deviceInfoSet,
        IntPtr deviceInfoData,
        ref Guid interfaceClassGuid,
        uint memberIndex,
        ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailW(
        IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData,
        int deviceInterfaceDetailDataSize,
        out int requiredSize,
        IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);
}
