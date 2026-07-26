using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace CtrlAgent.Gui;

/// <summary>
/// The shared animated depth layer. Drop it as the first child of a window's
/// root panel; it is hit-test invisible and clips to its own bounds, so it can
/// never intercept input or bleed past the surface it sits on.
/// </summary>
public partial class AmbientBackdrop : UserControl
{
    public AmbientBackdrop() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
