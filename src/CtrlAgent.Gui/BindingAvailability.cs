using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>
/// Whether a binding can fire right now — the one rule both shortcut
/// surfaces (the main window's hub list and Mainframe's HUD) filter by, kept
/// in lockstep with the engine's approval lockout. A listed shortcut that
/// cannot fire is a lie: the hub once showed "A · Submit prompt" during an
/// approval, first in the list, while the engine suppressed exactly that.
/// </summary>
internal static class BindingAvailability
{
    public static bool IsAvailable(BindingRow binding, bool pending, bool busy)
    {
        if (binding.IsApproval)
        {
            return pending;
        }

        if (pending)
        {
            return false;
        }

        return binding.Command != AgentCommandKind.Interrupt || busy;
    }
}
