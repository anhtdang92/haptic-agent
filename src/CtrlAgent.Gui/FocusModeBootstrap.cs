using System.Runtime.CompilerServices;
using Avalonia;

namespace CtrlAgent.Gui;

internal static class FocusModeBootstrap
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainframeWindow>(
            static (window, _) => FocusModeOverlay.Attach(window, mainframe: true));
    }
}
