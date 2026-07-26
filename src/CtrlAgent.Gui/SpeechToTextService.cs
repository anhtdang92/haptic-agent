using System.Speech.Recognition;

namespace CtrlAgent.Gui;

/// <summary>
/// Single-shot Windows dictation for Mainframe voice prompts, built on the
/// in-box System.Speech recognizer: offline, no cloud account, no package
/// identity requirements. Initialization is lazy and failure is a state, not
/// an exception — no microphone or no recognizer simply reports unavailable.
/// Events are raised on recognizer threads; callers marshal to the UI.
/// </summary>
public sealed class SpeechToTextService : IDisposable
{
    private SpeechRecognitionEngine? _recognizer;
    private TaskCompletionSource<string?>? _pending;
    private bool _disposed;

    /// <summary>Partial text while the user is still speaking.</summary>
    public event Action<string>? HypothesisChanged;

    /// <summary>Why initialization failed, or null while unattempted/healthy.</summary>
    public string? UnavailableReason { get; private set; }

    public bool EnsureInitialized()
    {
        if (_recognizer is not null)
        {
            return true;
        }

        if (_disposed || UnavailableReason is not null)
        {
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            UnavailableReason = "Speech recognition requires Windows.";
            return false;
        }

        try
        {
            var recognizer = new SpeechRecognitionEngine();
            recognizer.LoadGrammar(new DictationGrammar());
            recognizer.SetInputToDefaultAudioDevice();
            recognizer.SpeechHypothesized += (_, eventArgs) =>
                HypothesisChanged?.Invoke(eventArgs.Result.Text);
            recognizer.SpeechRecognized += (_, eventArgs) =>
                _pending?.TrySetResult(eventArgs.Result.Text);
            // Fires on silence timeout, cancellation, and audio problems alike.
            recognizer.RecognizeCompleted += (_, eventArgs) =>
                _pending?.TrySetResult(eventArgs.Result?.Text);

            _recognizer = recognizer;
            return true;
        }
        catch (Exception exception)
        {
            UnavailableReason = exception.Message;
            return false;
        }
    }

    /// <summary>
    /// Listens for one utterance and returns its text, or null on silence,
    /// cancellation, or an unavailable recognizer.
    /// </summary>
    public Task<string?> RecognizeOnceAsync()
    {
        if (!EnsureInitialized() || _recognizer is null)
        {
            return Task.FromResult<string?>(null);
        }

        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = completion;
        try
        {
            _recognizer.RecognizeAsync(RecognizeMode.Single);
        }
        catch (Exception exception)
        {
            UnavailableReason = exception.Message;
            completion.TrySetResult(null);
        }

        return completion.Task;
    }

    public void CancelRecognition()
    {
        try
        {
            _recognizer?.RecognizeAsyncCancel();
        }
        catch (InvalidOperationException)
        {
        }

        _pending?.TrySetResult(null);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelRecognition();
        _recognizer?.Dispose();
        _recognizer = null;
    }
}
