using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace CtrlAgent.Gui;

/// <summary>One audio capture device, as winmm reports it. The name is
/// truncated to 31 characters by the API — a Windows limitation, matched by
/// selection comparing these same truncated names.</summary>
public sealed record MicrophoneInfo(int Index, string Name);

/// <summary>
/// Enumerates capture devices through winmm — the same dependency-free route
/// the boot chime plays through. This exists because System.Speech can only
/// listen to "the default device" or "a stream": there is no API to pick a
/// microphone, so an in-app picker has to capture from the chosen device
/// itself (<see cref="WaveInStream"/>) and hand the recognizer the stream.
/// </summary>
public static class MicrophoneCatalog
{
    public static IReadOnlyList<MicrophoneInfo> List()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        var devices = new List<MicrophoneInfo>();
        var count = WaveInNative.waveInGetNumDevs();
        for (uint index = 0; index < count; index++)
        {
            if (WaveInNative.waveInGetDevCapsW(
                    (UIntPtr)index,
                    out var capabilities,
                    (uint)Marshal.SizeOf<WaveInNative.WaveInCaps>()) == 0)
            {
                var name = capabilities.ProductName?.TrimEnd('\0').Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    devices.Add(new MicrophoneInfo((int)index, name));
                }
            }
        }

        return devices;
    }
}

/// <summary>
/// A blocking PCM stream captured live from one winmm device: 16 kHz, 16-bit,
/// mono — the format the in-box recognizer expects. Buffers are returned to
/// the driver from our own polling thread (CALLBACK_EVENT), never from a
/// driver callback, because winmm deadlocks if wave functions are called
/// re-entrantly from its callback thread.
/// </summary>
public sealed class WaveInStream : Stream
{
    private const int SampleRate = 16000;
    private const int BufferBytes = 3200; // 100 ms of 16-bit mono
    private const int BufferCount = 8;
    private const uint CallbackEvent = 0x00050000;
    private const uint DoneFlag = 0x00000001;

    private readonly IntPtr _device;
    private readonly IntPtr[] _headers = new IntPtr[BufferCount];
    private readonly AutoResetEvent _signal = new(false);
    private readonly ConcurrentQueue<byte[]> _chunks = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly Thread _pump;
    private volatile bool _closed;
    private byte[]? _current;
    private int _currentOffset;

    public WaveInStream(int deviceIndex)
    {
        var format = new WaveInNative.WaveFormat
        {
            FormatTag = 1, // PCM
            Channels = 1,
            SamplesPerSecond = SampleRate,
            AverageBytesPerSecond = SampleRate * 2,
            BlockAlign = 2,
            BitsPerSample = 16,
            ExtraSize = 0,
        };

        var opened = WaveInNative.waveInOpen(
            out _device,
            (uint)deviceIndex,
            ref format,
            _signal.SafeWaitHandle.DangerousGetHandle(),
            IntPtr.Zero,
            CallbackEvent);
        if (opened != 0)
        {
            throw new InvalidOperationException($"waveInOpen failed for device {deviceIndex} (code {opened}).");
        }

        for (var index = 0; index < BufferCount; index++)
        {
            var header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveInNative.WaveHeader>());
            var data = Marshal.AllocHGlobal(BufferBytes);
            var value = new WaveInNative.WaveHeader { Data = data, BufferLength = BufferBytes };
            Marshal.StructureToPtr(value, header, fDeleteOld: false);
            WaveInNative.waveInPrepareHeader(_device, header, (uint)Marshal.SizeOf<WaveInNative.WaveHeader>());
            WaveInNative.waveInAddBuffer(_device, header, (uint)Marshal.SizeOf<WaveInNative.WaveHeader>());
            _headers[index] = header;
        }

        _pump = new Thread(Pump) { IsBackground = true, Name = "waveIn pump" };
        _pump.Start();
        WaveInNative.waveInStart(_device);
    }

    private void Pump()
    {
        while (!_closed)
        {
            _signal.WaitOne(TimeSpan.FromMilliseconds(250));
            foreach (var header in _headers)
            {
                if (_closed)
                {
                    return;
                }

                var value = Marshal.PtrToStructure<WaveInNative.WaveHeader>(header);
                if ((value.Flags & DoneFlag) == 0)
                {
                    continue;
                }

                if (value.BytesRecorded > 0)
                {
                    var chunk = new byte[value.BytesRecorded];
                    Marshal.Copy(value.Data, chunk, 0, chunk.Length);
                    _chunks.Enqueue(chunk);
                    _available.Release();
                }

                value.Flags &= ~DoneFlag;
                value.BytesRecorded = 0;
                Marshal.StructureToPtr(value, header, fDeleteOld: false);
                WaveInNative.waveInAddBuffer(_device, header, (uint)Marshal.SizeOf<WaveInNative.WaveHeader>());
            }
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (true)
        {
            if (_current is { } chunk)
            {
                var copied = Math.Min(count, chunk.Length - _currentOffset);
                Array.Copy(chunk, _currentOffset, buffer, offset, copied);
                _currentOffset += copied;
                if (_currentOffset >= chunk.Length)
                {
                    _current = null;
                    _currentOffset = 0;
                }

                return copied;
            }

            if (_chunks.TryDequeue(out var next))
            {
                _current = next;
                _currentOffset = 0;
                continue;
            }

            if (_closed)
            {
                return 0; // End of stream: recognition stops cleanly.
            }

            _available.Wait();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (!_closed)
        {
            _closed = true;
            WaveInNative.waveInStop(_device);
            WaveInNative.waveInReset(_device);
            _signal.Set();
            _available.Release();
            if (_pump.IsAlive)
            {
                _pump.Join(TimeSpan.FromSeconds(1));
            }

            foreach (var header in _headers)
            {
                var value = Marshal.PtrToStructure<WaveInNative.WaveHeader>(header);
                WaveInNative.waveInUnprepareHeader(_device, header, (uint)Marshal.SizeOf<WaveInNative.WaveHeader>());
                Marshal.FreeHGlobal(value.Data);
                Marshal.FreeHGlobal(header);
            }

            WaveInNative.waveInClose(_device);
            _signal.Dispose();
        }

        base.Dispose(disposing);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class WaveInNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WaveInCaps
    {
        public ushort ManufacturerId;
        public ushort ProductId;
        public uint DriverVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string ProductName;

        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
    }

    [DllImport("winmm.dll")]
    public static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    public static extern uint waveInGetDevCapsW(UIntPtr deviceId, out WaveInCaps capabilities, uint size);

    [DllImport("winmm.dll")]
    public static extern uint waveInOpen(
        out IntPtr handle,
        uint deviceId,
        ref WaveFormat format,
        IntPtr callback,
        IntPtr instance,
        uint flags);

    [DllImport("winmm.dll")]
    public static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    public static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    public static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);

    [DllImport("winmm.dll")]
    public static extern uint waveInStart(IntPtr handle);

    [DllImport("winmm.dll")]
    public static extern uint waveInStop(IntPtr handle);

    [DllImport("winmm.dll")]
    public static extern uint waveInReset(IntPtr handle);

    [DllImport("winmm.dll")]
    public static extern uint waveInClose(IntPtr handle);
}
