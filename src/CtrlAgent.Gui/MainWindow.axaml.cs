using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia.Controls;
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

        // F11 = enter Mainframe, the standard fullscreen key.
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.F11)
            {
                eventArgs.Handled = true;
                (Avalonia.Application.Current as App)?.ShowMainframe();
            }
            else if (eventArgs.Key == Key.Escape &&
                     DataContext is MainViewModel { IsDiffVisible: true } viewModel)
            {
                eventArgs.Handled = true;
                viewModel.CloseDiffCommand.Execute(null);
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
        viewModel.OutputScrollRequested += OnOutputScroll;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        StickToBottom(EventStream, eventArgs);

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs) =>
        StickToBottom(ChatList, eventArgs);

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
