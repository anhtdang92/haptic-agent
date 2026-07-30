namespace CtrlAgent.Gui;

/// <summary>
/// Transport-neutral speech language preference. Keeping this outside the
/// Windows recognition service lets settings and headless UI validation share
/// the same contract without pretending a microphone exists.
/// </summary>
public static class SpeechLanguageSettings
{
    public static string? Language { get; set; }
}
