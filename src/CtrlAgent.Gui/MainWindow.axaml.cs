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
