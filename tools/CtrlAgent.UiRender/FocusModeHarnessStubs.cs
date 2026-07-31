using Avalonia.Controls;

namespace CtrlAgent.Gui;

/// <summary>
/// The real overlay is a code-only window decoration and is compile-checked by
/// the Windows managed job. Headless screenshots keep their existing pixel
/// baselines while the focus policy/dashboard logic is covered by Core tests.
/// </summary>
public static class FocusModeOverlay
{
    public static void Attach(Window window, bool mainframe = false) { }
}
