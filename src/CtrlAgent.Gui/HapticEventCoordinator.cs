using System.Runtime.CompilerServices;
using CtrlAgent.Core;
using CtrlAgent.Hosting;

namespace CtrlAgent.Gui;

/// <summary>
/// Completes the semantic haptic wiring using HostEngine's existing public
/// events. Agent-state haptics remain owned by HostEngine; this coordinator adds
/// controller, command, queue, voice, settings, navigation, and failure cues.
/// </summary>
public sealed class HapticEventCoordinator
{
    private static readonly ConditionalWeakTable<HostEngine, HapticEventCoordinator> Attached = new();
    private static readonly object VoiceSync = new();
    private static WeakReference<HostEngine>? _voiceEngine;
    private readonly HostEngine _engine;
    private readonly FeedbackRouter _router = new();
    private int _lastQueueCount;

    static HapticEventCoordinator()
    {
        SpeechToTextService.AttemptStarted += () => PlayVoice(HapticPatternCatalog.VoiceListening);
        SpeechToTextService.ResultAvailable += result => PlayVoice(
            result.Succeeded ? HapticPatternCatalog.VoiceRecognized : HapticPatternCatalog.VoiceFailed);
    }

    private HapticEventCoordinator(HostEngine engine)
    {
        _engine = engine;
        engine.ControllerConnected += _ => Play(HapticPatternCatalog.Connected);
        engine.ControllerInputReceived += OnControllerInput;
        engine.LogEmitted += OnLog;
        engine.PromptQueueChanged += OnQueueChanged;
        engine.OutputScrollRequested += _ => Play(HapticPatternCatalog.NavigationTick);
        engine.VoicePromptRequested += () => Play(HapticPatternCatalog.VoiceListening);
        engine.ProfileApplied += _ => Play(HapticPatternCatalog.CommandAccepted);
        engine.SessionSettingsChanged += _ => Play(HapticPatternCatalog.CommandAccepted);
    }

    public static void Attach(HostEngine engine)
    {
        ArgumentNullException.ThrowIfNull(engine);
        _ = Attached.GetValue(engine, static candidate => new HapticEventCoordinator(candidate));
        lock (VoiceSync)
        {
            _voiceEngine = new WeakReference<HostEngine>(engine);
        }
    }

    private void OnControllerInput(ControllerInputEvent inputEvent)
    {
        if (inputEvent.Kind == ControllerInputEventKind.Connected)
        {
            Play(HapticPatternCatalog.Connected);
            return;
        }
        if (inputEvent.Kind == ControllerInputEventKind.Disconnected)
        {
            Play(HapticPatternCatalog.Disconnected);
            return;
        }

        // UI navigation is not dispatched as an agent command while input is
        // captured, so raw directional input is the authoritative cue source.
        if (_engine.InputCaptured && inputEvent.Kind == ControllerInputEventKind.Pressed &&
            inputEvent.Control is ControllerControl.DPadUp or ControllerControl.DPadDown or
                                  ControllerControl.DPadLeft or ControllerControl.DPadRight)
        {
            Play(HapticPatternCatalog.NavigationTick);
        }
    }

    private void OnQueueChanged(int count)
    {
        if (count > _lastQueueCount)
        {
            Play(HapticPatternCatalog.PromptQueued);
        }
        else if (count < _lastQueueCount)
        {
            Play(HapticPatternCatalog.CommandAccepted);
        }
        _lastQueueCount = count;
    }

    private void OnLog(string message)
    {
        const string commandPrefix = "[command] ";
        if (message.StartsWith(commandPrefix, StringComparison.Ordinal) &&
            Enum.TryParse<AgentCommandKind>(message[commandPrefix.Length..], true, out var command))
        {
            var pattern = _router.Route(command);
            if (pattern is not null) Play(pattern);
            return;
        }

        if (message.Contains("queue is full", StringComparison.OrdinalIgnoreCase))
        {
            Play(HapticPatternCatalog.QueueFull);
        }
        else if (message.Contains("prompt queued", StringComparison.OrdinalIgnoreCase))
        {
            Play(HapticPatternCatalog.PromptQueued);
        }
        else if (message.Contains("Sending queued prompt", StringComparison.OrdinalIgnoreCase))
        {
            Play(HapticPatternCatalog.CommandAccepted);
        }
        else if (message.StartsWith("[error]", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("connection lost", StringComparison.OrdinalIgnoreCase))
        {
            Play(HapticPatternCatalog.Error);
        }
    }

    private void Play(HapticPattern pattern) =>
        _ = _engine.PlayPatternAsync(pattern).AsTask();

    private static void PlayVoice(HapticPattern pattern)
    {
        HostEngine? engine = null;
        lock (VoiceSync)
        {
            _voiceEngine?.TryGetTarget(out engine);
        }
        if (engine is not null)
        {
            _ = engine.PlayPatternAsync(pattern).AsTask();
        }
    }
}
