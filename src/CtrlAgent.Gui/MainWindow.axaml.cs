using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CtrlAgent.Gui;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _observedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => ObserveLog();

        // Tunnel, not bubble: with AcceptsReturn the TextBox handles Enter
        // itself and marks the event handled, so a bubbling handler (or a
        // KeyBinding) never sees the key we want to intercept.
        PromptBox.AddHandler(KeyDownEvent, OnPromptKeyDown, RoutingStrategies.Tunnel);

        // Desktop-first: the keyboard reaches everything the mouse can.
        // F1 shortcuts map, F3 diff review, Ctrl+N new session, F11
        // Mainframe, Esc closes whichever overlay is on top.
        KeyDown += (_, eventArgs) =>
        {
            if (DataContext is not MainViewModel viewModel)
            {
                return;
            }

            switch (eventArgs.Key)
            {
                case Key.F11:
                    eventArgs.Handled = true;
                    (Avalonia.Application.Current as App)?.ShowMainframe();
                    break;

                case Key.F1:
                    eventArgs.Handled = true;
                    viewModel.IsShortcutsVisible = !viewModel.IsShortcutsVisible;
                    break;

                case Key.F3:
                    eventArgs.Handled = true;
                    viewModel.ShowDiffCommand.Execute(null);
                    break;

                case Key.N when eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control):
                    eventArgs.Handled = true;
                    viewModel.NewSessionCommand.Execute(null);
                    break;

                case Key.Escape when viewModel.IsShortcutsVisible:
                    eventArgs.Handled = true;
                    viewModel.CloseShortcutsCommand.Execute(null);
                    break;

                case Key.Escape when viewModel.IsDiffVisible:
                    eventArgs.Handled = true;
                    viewModel.CloseDiffCommand.Execute(null);
                    break;

                // The keyboard's coarse gear, matching the Mainframe d-pad:
                // Ctrl+arrows step between files while the diff is open.
                case Key.Down when viewModel.IsDiffVisible &&
                                   eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control):
                    eventArgs.Handled = true;
                    viewModel.StepDiffFile(+1);
                    break;

                case Key.Up when viewModel.IsDiffVisible &&
                                 eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control):
                    eventArgs.Handled = true;
                    viewModel.StepDiffFile(-1);
                    break;
            }
        };
    }

    /// <summary>
    /// Pages the surface on top when a binding scrolls the output: the diff
    /// panel while it is open, the conversation otherwise. 80% of a viewport
    /// per flick, matching the Mainframe feed.
    /// </summary>
    private void OnOutputScroll(int direction)
    {
        var scroll = DataContext is MainViewModel { IsDiffVisible: true }
            ? DiffScroller
            : ChatList.Scroll as ScrollViewer;
        if (scroll is null)
        {
            return;
        }

        var page = scroll.Viewport.Height * 0.8;
        var target = Math.Clamp(
            scroll.Offset.Y + direction * page,
            0,
            Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        scroll.Offset = scroll.Offset.WithY(target);
    }

    // Enter sends; Shift+Enter falls through to the TextBox and becomes a
    // newline. Sending on plain Enter is the habit every chat client teaches,
    // and a prompt is far more often one line than several.
    private void OnPromptKeyDown(object? sender, KeyEventArgs eventArgs)
    {
        if (eventArgs.Key != Key.Enter || eventArgs.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            return;
        }

        eventArgs.Handled = true;
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.SubmitPromptCommand.Execute(null);
        }
    }

    // Keeps the event stream pinned to the newest entry.
    private void ObserveLog()
    {
        if (DataContext is not MainViewModel viewModel || ReferenceEquals(viewModel, _observedViewModel))
        {
            return;
        }

        _observedViewModel = viewModel;
        viewModel.Log.CollectionChanged += OnLogChanged;
        viewModel.Transcript.CollectionChanged += OnTranscriptChanged;
        viewModel.TranscriptStreamed += FollowChatIfAtBottom;
        viewModel.OutputScrollRequested += OnOutputScroll;
        viewModel.ModelPickerRequested += OnModelPickerRequested;
        viewModel.DiffJumpRequested += OnDiffJump;
    }

    /// <summary>
    /// Scrolls the diff to a file's header row — the rail click and the d-pad
    /// file step both land here. The row's own position is measured rather
    /// than estimated: rows vary in height (headers carry margins), so
    /// index-times-line-height would drift on long diffs.
    /// </summary>
    private void OnDiffJump(int rowIndex)
    {
        if (DiffList.ContainerFromIndex(rowIndex) is not Control container ||
            container.TranslatePoint(new Avalonia.Point(0, 0), DiffList) is not { } point)
        {
            return;
        }

        var highest = Math.Max(0, DiffScroller.Extent.Height - DiffScroller.Viewport.Height);
        DiffScroller.Offset = DiffScroller.Offset.WithY(Math.Clamp(point.Y, 0, highest));
    }

    // Typed "/model" means "show me the picker" — open the chip's flyout
    // exactly as if it had been clicked.
    private void OnModelPickerRequested() => ModelKnob.Flyout?.ShowAt(ModelKnob);

    // Rebuilt on every open so a headset plugged in mid-session appears
    // without a restart.
    private void OnPickMicrophone(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is MainViewModel viewModel)
        {
            viewModel.RefreshMicrophones();
        }

        FlyoutBase.ShowAttachedFlyout(MicPickButton);
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        StickToBottom(EventStream, eventArgs);

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action == NotifyCollectionChangedAction.Add)
        {
            FollowChatIfAtBottom();
        }
    }

    /// <summary>
    /// Keeps the conversation pinned to the newest words — including while a
    /// streaming reply grows its bubble <em>in place</em>. ScrollIntoView is
    /// the wrong tool for that: a growing last bubble is always partially
    /// visible, so "into view" is already satisfied and it never moves, while
    /// the newest text streams in below the fold. Stickiness is decided here,
    /// before layout absorbs the growth (the extent still reflects the state
    /// the user actually saw); the scroll itself is posted for after layout,
    /// when ScrollToEnd knows the new bottom.
    /// </summary>
    private void FollowChatIfAtBottom()
    {
        if (ChatList.Scroll is not ScrollViewer scroll)
        {
            return;
        }

        // Reading history must not be undone by the next chunk; scrolling
        // back down re-arms following. The tolerance covers a partially
        // visible last row.
        if (scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 48)
        {
            return;
        }

        Avalonia.Threading.Dispatcher.UIThread.Post(
            scroll.ScrollToEnd,
            Avalonia.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// Follows new items only while the view is already at the bottom. It used
    /// to follow unconditionally, so scrolling up to read what the agent did
    /// two minutes ago was undone by the next event — and during a busy turn
    /// that is several times a second, which made the history unreadable
    /// exactly when you most wanted to read it. Scrolling back down re-arms it.
    /// </summary>
    private static void StickToBottom(ListBox list, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action != NotifyCollectionChangedAction.Add || list.ItemCount == 0)
        {
            return;
        }

        // Measured before layout runs for the new item, so the extent is still
        // the pre-add one. The tolerance covers partially-visible last rows.
        if (list.Scroll is { } scroll &&
            scroll.Offset.Y < scroll.Extent.Height - scroll.Viewport.Height - 32)
        {
            return;
        }

        list.ScrollIntoView(list.ItemCount - 1);
    }

    private async void OnBrowseWorkingDirectory(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose the repository CtrlAgent should work in",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            viewModel.SetupWorkingDirectory = path;
        }
    }

    private void OnValidateHardware(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel)
        {
            return;
        }

        var wizardExecutable = Path.Combine(AppContext.BaseDirectory, "CtrlAgent.App.exe");
        if (!File.Exists(wizardExecutable))
        {
            viewModel.AppendLog(
                "CtrlAgent.App.exe was not found next to the GUI. Run the wizard manually: " +
                "dotnet run --project src/CtrlAgent.App -- --validate");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = wizardExecutable,
                ArgumentList = { "--validate" },
                UseShellExecute = true,
            });
            viewModel.AppendLog(
                "Validation wizard launched in a separate console. If it cannot see the controller, " +
                "exit the GUI first — some device paths are exclusive.");
        }
        catch (Exception exception)
        {
            viewModel.AppendLog($"Could not launch the wizard: {exception.Message}");
        }
    }

    // The hero band is the drag handle now that the client area covers the
    // title bar. Buttons inside it swallow their own pointer events first.
    private void OnHeroPressed(object? sender, PointerPressedEventArgs eventArgs)
    {
        if (eventArgs.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(eventArgs);
        }
    }

    private void OnToggleOverlay(object? sender, RoutedEventArgs eventArgs) =>
        (Avalonia.Application.Current as App)?.ToggleOverlay();

    private void OnShowMainframe(object? sender, RoutedEventArgs eventArgs) =>
        (Avalonia.Application.Current as App)?.ShowMainframe();

    private async void OnEditProfile(object? sender, RoutedEventArgs eventArgs)
    {
        if (DataContext is not MainViewModel viewModel || viewModel.Engine is null)
        {
            return;
        }

        var editor = new ProfileEditorWindow(viewModel.Engine);
        await editor.ShowDialog(this);
    }
}
