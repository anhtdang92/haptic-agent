using System.Runtime.InteropServices;

namespace CtrlAgent.Gui;

/// <summary>
/// The Cockpit boot sound: a low swell rising into three bell notes,
/// synthesized to a WAV in memory (no audio asset to ship) and played
/// through winmm — dependency-free and Windows-only. Failure is silent;
/// a missing audio device must never block the intro.
/// </summary>
internal static class BootChime
{
    private const int SampleRate = 44100;
    private const double Seconds = 2.2;

    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;

    // winmm reads the buffer during async playback; keep it rooted.
    private static byte[]? _wav;

    public static void Play()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            _wav ??= Synthesize();
            _ = PlaySound(_wav, IntPtr.Zero, SndAsync | SndNoDefault | SndMemory);
        }
        catch (Exception)
        {
        }
    }

    [DllImport("winmm.dll")]
    private static extern bool PlaySound(byte[] sound, IntPtr module, uint flags);

    private static byte[] Synthesize()
    {
        var sampleCount = (int)(SampleRate * Seconds);
        var samples = new double[sampleCount];

        // Low swell: a sine sweeping an octave up, breathing in and out.
        var phase = 0.0;
        for (var index = 0; index < sampleCount; index++)
        {
            var time = index / (double)SampleRate;
            if (time > 1.6)
            {
                break;
            }

            var sweep = Math.Clamp(time / 1.3, 0, 1);
            var frequency = 96 + (96 * sweep);
            phase += 2 * Math.PI * frequency / SampleRate;

            var envelope = time < 0.9
                ? Math.Pow(Math.Sin(time / 0.9 * Math.PI / 2), 2) * 0.30
                : Math.Max(0, 0.30 * (1 - ((time - 0.9) / 0.7)));
            samples[index] += Math.Sin(phase) * envelope;
        }

        // Three bell notes stepping upward as the swell fades.
        AddBell(samples, startSeconds: 0.75, frequency: 440.00, amplitude: 0.20);
        AddBell(samples, startSeconds: 1.00, frequency: 587.33, amplitude: 0.22);
        AddBell(samples, startSeconds: 1.25, frequency: 880.00, amplitude: 0.24);

        // Soft-clip and quantize to 16-bit PCM.
        var pcm = new byte[sampleCount * 2];
        for (var index = 0; index < sampleCount; index++)
        {
            var value = Math.Tanh(samples[index]);
            var quantized = (short)(value * short.MaxValue * 0.9);
            pcm[index * 2] = (byte)(quantized & 0xFF);
            pcm[(index * 2) + 1] = (byte)((quantized >> 8) & 0xFF);
        }

        return WrapWav(pcm);
    }

    private static void AddBell(double[] samples, double startSeconds, double frequency, double amplitude)
    {
        var start = (int)(startSeconds * SampleRate);
        for (var index = start; index < samples.Length; index++)
        {
            var time = (index - start) / (double)SampleRate;
            var envelope = amplitude * Math.Exp(-3.2 * time);
            if (envelope < 0.001)
            {
                break;
            }

            var fundamental = Math.Sin(2 * Math.PI * frequency * time);
            var shimmer = 0.3 * Math.Sin(2 * Math.PI * frequency * 2.0 * time);
            samples[index] += (fundamental + shimmer) * envelope;
        }
    }

    private static byte[] WrapWav(byte[] pcm)
    {
        using var stream = new MemoryStream(44 + pcm.Length);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(SampleRate);
        writer.Write(SampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        writer.Flush();
        return stream.ToArray();
    }
}
