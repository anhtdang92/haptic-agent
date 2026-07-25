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

        // F11 = enter Cockpit, the standard fullscreen key.
        KeyDown += (_, eventArgs) =>
        {
            if (eventArgs.Key == Key.F11)
            {
                eventArgs.Handled = true;
                (Avalonia.Application.Current as App)?.ShowCockpit();
            }
        };
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
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action == NotifyCollectionChangedAction.Add && EventStream.ItemCount > 0)
        {
            EventStream.ScrollIntoView(EventStream.ItemCount - 1);
        }
    }

    private void OnTranscriptChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (eventArgs.Action == NotifyCollectionChangedAction.Add && ChatList.ItemCount > 0)
        {
            ChatList.ScrollIntoView(ChatList.ItemCount - 1);
        }
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

    private void OnShowCockpit(object? sender, RoutedEventArgs eventArgs) =>
        (Avalonia.Application.Current as App)?.ShowCockpit();

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
