using System.Collections.Specialized;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

public sealed partial class MainWindow : Window
{
    private MainViewModel? _observedViewModel;

    public MainWindow()
    {
        InitializeComponent();
        FocusModeOverlay.Attach(this);
        DataContextChanged += (_, _) => ObserveLog();

        // Mission control's keyboard: F1 shortcuts map, F2 focus mode,
        // Ctrl+N new session, F11 Mainframe, Esc closes shortcuts.
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

                case Key.F2:
                    eventArgs.Handled = true;
                    GuiSettings.TrySaveFocusMode(FocusContractSettings.Next());
                    break;

                case Key.N when eventArgs.KeyModifiers.HasFlag(KeyModifiers.Control):
                    eventArgs.Handled = true;
                    viewModel.NewSessionCommand.Execute(null);
                    break;

                case Key.Escape when viewModel.IsShortcutsVisible:
                    eventArgs.Handled = true;
                    viewModel.CloseShortcutsCommand.Execute(null);
                    break;
            }
        };
    }

    private void OnOutputScroll(int direction)
    {
        if (EventStream.Scroll is not ScrollViewer scroll)
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

    private void ObserveLog()
    {
        if (DataContext is not MainViewModel viewModel || ReferenceEquals(viewModel, _observedViewModel))
        {
            return;
        }

        _observedViewModel = viewModel;
        viewModel.Log.CollectionChanged += OnLogChanged;
        viewModel.OutputScrollRequested += OnOutputScroll;
        viewModel.ModelPickerRequested += OnModelPickerRequested;
    }

    private void OnModelPickerRequested() => ModelKnob.Flyout?.ShowAt(ModelKnob);

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

    private static void StickToBottom(ListBox list, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action != NotifyCollectionChangedAction.Add || list.ItemCount == 0)
        {
            return;
        }

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
