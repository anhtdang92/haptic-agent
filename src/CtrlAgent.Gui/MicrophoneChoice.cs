using System.Windows.Input;

namespace CtrlAgent.Gui;

/// <summary>One row of the mic-picker flyout. Carries its own command, like
/// the session rows, so the flyout template stays a pure binding.</summary>
public sealed class MicrophoneChoice
{
    public required string Label { get; init; }

    public required ICommand SelectCommand { get; init; }

    public static MicrophoneChoice For(string? deviceName, bool isCurrent, Action<string?> select) => new()
    {
        // "• " marks the active choice; a bullet because Inter has one and a
        // checkmark glyph is exactly the class of character that renders as
        // tofu (see the transcript markers).
        Label = (isCurrent ? "• " : "  ") + (deviceName ?? "System default"),
        SelectCommand = new RelayCommand(_ => select(deviceName)),
    };
}
