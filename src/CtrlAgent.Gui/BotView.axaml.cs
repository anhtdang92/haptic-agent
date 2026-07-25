using Avalonia.Controls;
using Avalonia.Input;

namespace CtrlAgent.Gui;

/// <summary>The shared CTRL·BOT drawing; clicking the bot pokes it.</summary>
public sealed partial class BotView : UserControl
{
    public BotView()
    {
        InitializeComponent();
    }

    private void OnPoke(object? sender, PointerPressedEventArgs eventArgs) =>
        (DataContext as AgentBuddyViewModel)?.Poke();
}
