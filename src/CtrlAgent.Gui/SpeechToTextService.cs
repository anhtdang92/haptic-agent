using System.Globalization;
using System.Speech.Recognition;

namespace CtrlAgent.Gui;

public sealed class SpeechToTextService : IDisposable
{
    private readonly SpeechAudioRecorder _recorder = new();
    private CancellationTokenSource? _active;
    private bool _disposed;

    public static string? PreferredMicrophone { get; set; }
    public static string? Vocabulary { get; set; }
    public static TimeSpan MaximumRecordingDuration { get; set; } = TimeSpan.FromSeconds(15);

    public static event Action? AttemptStarted;
    public static event Action<SpeechTranscriptionResult>? ResultAvailable;

    public event Action<string>? HypothesisChanged;

    public string? UnavailableReason { get; private set; }

    public bool EnsureInitialized()
    {
        if (_disposed)
        {
            UnavailableReason = "Speech service has been disposed.";
            return false;
        }
        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "Speech recognition requires Windows.";
            return false;
        }
        UnavailableReason = null;
        return true;
    }

    public async Task<SpeechTranscriptionResult> RecognizeDetailedAsync()
    {
        AttemptStarted?.Invoke();
        var result = await RecognizeCoreAsync().ConfigureAwait(false);
        ResultAvailable?.Invoke(result);
        return result;
    }

    private async Task<SpeechTranscriptionResult> RecognizeCoreAsync()
    {
        if (!EnsureInitialized())
        {
            return new(SpeechResultKind.Unavailable, Message: UnavailableReason);
        }

        CancelRecognition();
        _active = new CancellationTokenSource();
        var token = _active.Token;

        var recorded = await _recorder.RecordAsync(
            PreferredMicrophone,
            MaximumRecordingDuration,
            token).ConfigureAwait(false);

        if (recorded.Kind is SpeechResultKind.Cancelled or SpeechResultKind.CaptureFailed or SpeechResultKind.Unavailable)
        {
            return recorded;
        }
        if (string.IsNullOrWhiteSpace(recorded.Text))
        {
            return new(SpeechResultKind.NoSpeech, Message: recorded.Message ?? "No audio was recorded.");
        }

        byte[] waveAudio;
        try
        {
            waveAudio = Convert.FromBase64String(recorded.Text);
        }
        catch (FormatException)
        {
            return new(SpeechResultKind.CaptureFailed, Message: "Recorded audio was invalid.");
        }

        var provider = CreateProvider();
        try
        {
            var unavailable = await provider.CheckAvailabilityAsync(token).ConfigureAwait(false);
            if (unavailable is not null)
            {
                return new(SpeechResultKind.Unavailable, Message: unavailable);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            return await provider.TranscribeAsync(
                waveAudio,
                new SpeechProviderOptions(SpeechLanguageSettings.Language, Vocabulary, TimeSpan.FromSeconds(45)),
                timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return new(SpeechResultKind.TimedOut, Message: "Transcription timed out.");
        }
        finally
        {
            await provider.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<string?> RecognizeOnceAsync()
    {
        var result = await RecognizeDetailedAsync().ConfigureAwait(false);
        UnavailableReason = result.Succeeded ? null : result.Message;
        return result.Text;
    }

    private static ISpeechToTextProvider CreateProvider() => SpeechProviderSettings.Provider switch
    {
        SpeechProviderKind.OpenAi => new OpenAiSpeechToTextProvider(),
        SpeechProviderKind.LocalWhisper => new LocalWhisperSpeechToTextProvider(),
        _ => new WindowsSpeechToTextProvider(),
    };

    public void CancelRecognition()
    {
        try { _active?.Cancel(); } catch (ObjectDisposedException) { }
        _active?.Dispose();
        _active = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelRecognition();
    }
}

public sealed class WindowsSpeechToTextProvider : ISpeechToTextProvider
{
    public SpeechProviderKind Kind => SpeechProviderKind.Windows;
    public string DisplayName => "Windows Speech";

    public Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult<string?>("Windows Speech is only available on Windows.");
        }

        try
        {
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            return recognizers.Count == 0
                ? Task.FromResult<string?>(
                    "No Windows speech recognizer is installed. Install a Speech language pack or choose OpenAI/Local Whisper.")
                : Task.FromResult<string?>(null);
        }
        catch (Exception exception)
        {
            return Task.FromResult<string?>(exception.Message);
        }
    }

    public async Task<SpeechTranscriptionResult> TranscribeAsync(
        byte[] waveAudio,
        SpeechProviderOptions options,
        CancellationToken cancellationToken)
    {
        var root = Path.Combine(Path.GetTempPath(), "CtrlAgent-windows-speech-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var wavePath = Path.Combine(root, "input.wav");
        await File.WriteAllBytesAsync(wavePath, waveAudio, cancellationToken).ConfigureAwait(false);

        try
        {
            var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
            var requestedCulture = ParseCulture(options.Language);
            var recognizerInfo = requestedCulture is null
                ? recognizers.FirstOrDefault(info => info.Culture.Equals(CultureInfo.CurrentUICulture)) ?? recognizers.FirstOrDefault()
                : recognizers.FirstOrDefault(info => info.Culture.Name.Equals(requestedCulture.Name, StringComparison.OrdinalIgnoreCase));

            if (recognizerInfo is null)
            {
                return new(SpeechResultKind.Unavailable,
                    Message: requestedCulture is null
                        ? "No compatible Windows speech recognizer is installed."
                        : $"No Windows speech recognizer is installed for {requestedCulture.DisplayName}.");
            }

            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var recognizer = new SpeechRecognitionEngine(recognizerInfo);
                recognizer.LoadGrammar(new DictationGrammar());
                recognizer.SetInputToWaveFile(wavePath);
                var result = recognizer.Recognize(TimeSpan.FromSeconds(30));
                return string.IsNullOrWhiteSpace(result?.Text)
                    ? new SpeechTranscriptionResult(SpeechResultKind.NoSpeech, Message: "Windows Speech did not recognize an utterance.")
                    : new SpeechTranscriptionResult(SpeechResultKind.Recognized, result.Text.Trim());
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new(SpeechResultKind.Cancelled, Message: "Windows transcription was cancelled.");
        }
        catch (Exception exception)
        {
            return new(SpeechResultKind.TranscriptionFailed, Message: exception.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }

    private static CultureInfo? ParseCulture(string? language)
    {
        if (string.IsNullOrWhiteSpace(language) || language.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try { return CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { return null; }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
