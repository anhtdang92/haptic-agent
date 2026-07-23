using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using CtrlAgent.Core;
using CtrlAgent.Hosting;

namespace CtrlAgent.Gui;

public sealed partial class ProfileEditorWindow : Window
{
    private readonly HostEngine? _engine;
    private readonly ProfileEditorViewModel _viewModel;

    // For the XAML runtime loader/designer only; the app uses the engine ctor.
    public ProfileEditorWindow()
    {
        InitializeComponent();
        _viewModel = new ProfileEditorViewModel(ControllerProfile.Default);
        DataContext = _viewModel;
    }

    public ProfileEditorWindow(HostEngine engine)
        : this()
    {
        _engine = engine;
        _viewModel.LoadFrom(engine.Profile);
    }

    private void OnApply(object? sender, RoutedEventArgs eventArgs)
    {
        if (_engine is null || !_viewModel.TryBuildProfile(out var profile) || profile is null)
        {
            return;
        }

        if (!_engine.TryApplyProfile(profile, out var errors))
        {
            _viewModel.ShowExternalErrors(errors);
        }
    }

    private async void OnSave(object? sender, RoutedEventArgs eventArgs)
    {
        if (!_viewModel.TryBuildProfile(out var profile) || profile is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save controller profile",
            SuggestedFileName = $"{profile.Name}.json",
            DefaultExtension = "json",
            FileTypeChoices =
            [
                new FilePickerFileType("JSON profile") { Patterns = ["*.json"] },
            ],
        }).ConfigureAwait(true);

        if (file is null)
        {
            return;
        }

        await using var stream = await file.OpenWriteAsync().ConfigureAwait(true);
        await using var writer = new StreamWriter(stream);
        await writer.WriteAsync(ControllerProfileJson.Serialize(profile)).ConfigureAwait(true);
    }

    private async void OnLoad(object? sender, RoutedEventArgs eventArgs)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load controller profile",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON profile") { Patterns = ["*.json"] },
            ],
        }).ConfigureAwait(true);

        if (files.Count == 0)
        {
            return;
        }

        try
        {
            await using var stream = await files[0].OpenReadAsync().ConfigureAwait(true);
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync().ConfigureAwait(true);
            _viewModel.LoadFrom(ControllerProfileJson.Deserialize(json));
        }
        catch (Exception exception) when (exception is FormatException or IOException)
        {
            _viewModel.ShowExternalErrors([$"Could not load profile: {exception.Message}"]);
        }
    }

    private void OnClose(object? sender, RoutedEventArgs eventArgs) => Close();
}
