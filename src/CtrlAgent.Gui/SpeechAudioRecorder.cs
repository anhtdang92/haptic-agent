using System.Buffers.Binary;

namespace CtrlAgent.Gui;

public sealed class SpeechAudioRecorder
{
    private const int SampleRate = 16000;
    private const short Channels = 1;
    private const short BitsPerSample = 16;

    public async Task<SpeechTranscriptionResult> RecordAsync(
        string? preferredMicrophone,
        TimeSpan maximumDuration,
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new(SpeechResultKind.Unavailable, Message: "Microphone recording requires Windows.");
        }

        var devices = MicrophoneCatalog.List();
        var device = preferredMicrophone is null
            ? devices.FirstOrDefault()
            : devices.FirstOrDefault(candidate => string.Equals(
                candidate.Name, preferredMicrophone, StringComparison.OrdinalIgnoreCase));

        if (device is null)
        {
            return new(SpeechResultKind.CaptureFailed,
                Message: preferredMicrophone is null
                    ? "No microphone was found. Check Windows microphone privacy settings and the default input device."
                    : $"The selected microphone '{preferredMicrophone}' is unavailable. Choose another device.");
        }

        try
        {
            using var capture = new WaveInStream(device.Index);
            using var pcm = new MemoryStream();
            var buffer = new byte[3200];
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(maximumDuration);

            while (!timeout.IsCancellationRequested)
            {
                var readTask = Task.Run(() => capture.Read(buffer, 0, buffer.Length), timeout.Token);
                var read = await readTask.ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }
                await pcm.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new(SpeechResultKind.Cancelled, Message: "Recording was cancelled.");
            }

            if (pcm.Length == 0)
            {
                return new(SpeechResultKind.NoSpeech, Message: "The microphone produced no audio.");
            }

            return new(SpeechResultKind.Recognized, Convert.ToBase64String(ToWave(pcm.ToArray())));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(SpeechResultKind.Cancelled, Message: "Recording was cancelled.");
        }
        catch (OperationCanceledException)
        {
            return new(SpeechResultKind.TimedOut, Message: "Recording reached the time limit.");
        }
        catch (Exception exception)
        {
            return new(SpeechResultKind.CaptureFailed, Message: exception.Message);
        }
    }

    private static byte[] ToWave(byte[] pcm)
    {
        var output = new byte[44 + pcm.Length];
        "RIFF"u8.CopyTo(output);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(4), 36 + pcm.Length);
        "WAVEfmt "u8.CopyTo(output.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(22), Channels);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(28), SampleRate * Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(32), (short)(Channels * BitsPerSample / 8));
        BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(34), BitsPerSample);
        "data"u8.CopyTo(output.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(output.AsSpan(40), pcm.Length);
        pcm.CopyTo(output, 44);
        return output;
    }
}