using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using CtrlAgent.Hosting;

namespace CtrlAgent.Gui;

internal static class FocusModeBootstrap
{
    private static readonly ConditionalWeakTable<StyledElement, Observer> Observers = new();

    [ModuleInitializer]
    internal static void Initialize()
    {
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainWindow>(
            static (window, _) => Observe(window, mainframe: false));
        StyledElement.DataContextProperty.Changed.AddClassHandler<MainframeWindow>(
            static (window, _) => Observe(window, mainframe: true));
    }

    private static void Observe(StyledElement element, bool mainframe)
    {
        if (element is Avalonia.Controls.Window window)
        {
            FocusModeOverlay.Attach(window, mainframe);
        }

        var observer = Observers.GetValue(element, static owner => new Observer(owner));
        observer.Refresh();
    }

    private sealed class Observer
    {
        private readonly StyledElement _owner;
        private INotifyPropertyChanged? _viewModel;
        private HostEngine? _engine;

        public Observer(StyledElement owner)
        {
            _owner = owner;
        }

        public void Refresh()
        {
            var next = _owner.DataContext as INotifyPropertyChanged;
            if (!ReferenceEquals(next, _viewModel))
            {
                if (_viewModel is not null)
                {
                    _viewModel.PropertyChanged -= OnPropertyChanged;
                }
                _viewModel = next;
                if (_viewModel is not null)
                {
                    _viewModel.PropertyChanged += OnPropertyChanged;
                }
            }
            AttachEngine();
        }

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs) => AttachEngine();

        private void AttachEngine()
        {
            var engine = _owner.DataContext switch
            {
                MainViewModel main => main.Engine,
                MainframeViewModel frame => frame.Main.Engine,
                _ => null,
            };
            if (engine is null || ReferenceEquals(engine, _engine))
            {
                return;
            }
            _engine = engine;
            HapticEventCoordinator.Attach(engine);
        }
    }
}
