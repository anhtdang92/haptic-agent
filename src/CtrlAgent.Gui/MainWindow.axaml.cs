using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CtrlAgent.Gui;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
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

    private void OnToggleOverlay(object? sender, RoutedEventArgs eventArgs) =>
        (Avalonia.Application.Current as App)?.ToggleOverlay();

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
