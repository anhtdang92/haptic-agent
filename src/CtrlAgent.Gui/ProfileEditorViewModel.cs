using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CtrlAgent.Core;

namespace CtrlAgent.Gui;

/// <summary>One editable binding row. String-backed so partial input never throws.</summary>
public sealed class BindingEditor : ViewModelBase
{
    private string _control = nameof(ControllerControl.A);
    private string _gesture = nameof(InputGesture.Press);
    private string _command = nameof(AgentCommandKind.SubmitPrompt);
    private string _modifiersText = string.Empty;
    private string _holdMilliseconds = string.Empty;
    private string _doublePressMilliseconds = string.Empty;
    private string _minimumValue = string.Empty;
    private string _text = string.Empty;
    private string _layer = string.Empty;
    private bool _requiresPendingApproval;

    internal ProfileEditorViewModel? Owner { get; set; }

    public string Control
    {
        get => _control;
        set { if (Set(ref _control, value)) Changed(); }
    }

    public string Gesture
    {
        get => _gesture;
        set { if (Set(ref _gesture, value)) Changed(); }
    }

    public string Command
    {
        get => _command;
        set { if (Set(ref _command, value)) Changed(); }
    }

    /// <summary>Modifier controls separated by '+' (or ','), e.g. "leftShoulder".</summary>
    public string ModifiersText
    {
        get => _modifiersText;
        set { if (Set(ref _modifiersText, value)) Changed(); }
    }

    public string HoldMilliseconds
    {
        get => _holdMilliseconds;
        set { if (Set(ref _holdMilliseconds, value)) Changed(); }
    }

    public string DoublePressMilliseconds
    {
        get => _doublePressMilliseconds;
        set { if (Set(ref _doublePressMilliseconds, value)) Changed(); }
    }

    public string MinimumValue
    {
        get => _minimumValue;
        set { if (Set(ref _minimumValue, value)) Changed(); }
    }

    public string Text
    {
        get => _text;
        set { if (Set(ref _text, value)) Changed(); }
    }

    public bool RequiresPendingApproval
    {
        get => _requiresPendingApproval;
        set { if (Set(ref _requiresPendingApproval, value)) Changed(); }
    }

    /// <summary>Optional layer membership (must name a declared layer).</summary>
    public string Layer
    {
        get => _layer;
        set { if (Set(ref _layer, value)) Changed(); }
    }

    /// <summary>
    /// Reads like the rest of the app (P1, RB+A, "Approve once") rather than
    /// raw enum names, falling back to the typed text while it is still
    /// being edited.
    /// </summary>
    public string Summary
    {
        get
        {
            var control = Enum.TryParse<ControllerControl>(_control, ignoreCase: true, out var parsedControl)
                ? ControlLabels.Label(parsedControl)
                : _control;

            var modifiers = _modifiersText
                .Split(['+', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => Enum.TryParse<ControllerControl>(part, ignoreCase: true, out var parsed)
                    ? ControlLabels.Label(parsed)
                    : part)
                .ToArray();

            var chord = modifiers.Length > 0
                ? string.Join("+", modifiers) + "+" + control
                : control;

            var gesture = Enum.TryParse<InputGesture>(_gesture, ignoreCase: true, out var parsedGesture)
                ? ControlLabels.GestureSuffix(parsedGesture)
                : $" [{_gesture}]";

            var command = Enum.TryParse<AgentCommandKind>(_command, ignoreCase: true, out var parsedCommand)
                ? ControlLabels.Humanize(parsedCommand)
                : _command;

            var layer = _layer.Trim().Length > 0 ? $"  ⟨{_layer.Trim()}⟩" : string.Empty;
            return $"{chord}{gesture} → {command}{layer}";
        }
    }

    public InputBinding? ToBinding(int position, List<string> errors)
    {
        var valid = true;

        if (!Enum.TryParse<ControllerControl>(_control, ignoreCase: true, out var control) ||
            control == ControllerControl.None)
        {
            errors.Add($"Binding {position}: unknown control '{_control}'.");
            valid = false;
        }

        if (!Enum.TryParse<InputGesture>(_gesture, ignoreCase: true, out var gesture))
        {
            errors.Add($"Binding {position}: unknown gesture '{_gesture}'.");
            valid = false;
        }

        if (!Enum.TryParse<AgentCommandKind>(_command, ignoreCase: true, out var command))
        {
            errors.Add($"Binding {position}: unknown command '{_command}'.");
            valid = false;
        }

        HashSet<ControllerControl>? modifiers = null;
        if (!string.IsNullOrWhiteSpace(_modifiersText))
        {
            modifiers = [];
            foreach (var part in _modifiersText.Split(
                ['+', ','],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Enum.TryParse<ControllerControl>(part, ignoreCase: true, out var modifier) &&
                    modifier != ControllerControl.None)
                {
                    modifiers.Add(modifier);
                }
                else
                {
                    errors.Add($"Binding {position}: unknown modifier '{part}'.");
                    valid = false;
                }
            }
        }

        var hold = ParseMilliseconds(_holdMilliseconds, "hold", position, errors, ref valid);
        var window = ParseMilliseconds(_doublePressMilliseconds, "double-press", position, errors, ref valid);

        var minimum = 0.5f;
        if (!string.IsNullOrWhiteSpace(_minimumValue))
        {
            if (float.TryParse(_minimumValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                minimum = value;
            }
            else
            {
                errors.Add($"Binding {position}: minimum value '{_minimumValue}' is not a number.");
                valid = false;
            }
        }

        if (!valid)
        {
            return null;
        }

        return new InputBinding(
            control,
            gesture,
            command,
            modifiers is { Count: > 0 } ? modifiers : null,
            minimum,
            string.IsNullOrWhiteSpace(_text) ? null : _text,
            _requiresPendingApproval,
            hold,
            window,
            string.IsNullOrWhiteSpace(_layer) ? null : _layer.Trim());
    }

    private static TimeSpan? ParseMilliseconds(
        string input,
        string label,
        int position,
        List<string> errors,
        ref bool valid)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var milliseconds) &&
            milliseconds > 0)
        {
            return TimeSpan.FromMilliseconds(milliseconds);
        }

        errors.Add($"Binding {position}: {label} milliseconds '{input}' must be a positive integer.");
        valid = false;
        return null;
    }

    private void Changed()
    {
        Raise(nameof(Summary));
        Owner?.Revalidate();
    }
}

/// <summary>One editable profile layer: a name plus its activation rule.</summary>
public sealed class LayerEditor : ViewModelBase
{
    private string _name = "layer";
    private string _activation = nameof(LayerActivation.Always);

    internal ProfileEditorViewModel? Owner { get; set; }

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) Owner?.Revalidate(); }
    }

    public string Activation
    {
        get => _activation;
        set { if (Set(ref _activation, value)) Owner?.Revalidate(); }
    }

    public ProfileLayer ToLayer() => new(
        _name.Trim(),
        Enum.TryParse<LayerActivation>(_activation, ignoreCase: true, out var activation)
            ? activation
            : LayerActivation.Always);
}

public sealed class ProfileEditorViewModel : ViewModelBase
{
    private string _name = "custom";
    private BindingEditor? _selectedBinding;
    private bool _isValid;

    public ProfileEditorViewModel(ControllerProfile profile)
    {
        AddCommand = new RelayCommand(_ =>
        {
            var editor = new BindingEditor { Owner = this };
            Bindings.Add(editor);
            SelectedBinding = editor;
            Revalidate();
        });

        RemoveCommand = new RelayCommand(_ =>
        {
            if (SelectedBinding is { } selected)
            {
                Bindings.Remove(selected);
                SelectedBinding = Bindings.LastOrDefault();
                Revalidate();
            }
        });

        AddLayerCommand = new RelayCommand(_ =>
        {
            Layers.Add(new LayerEditor { Owner = this, Name = $"layer{Layers.Count + 1}" });
            Revalidate();
        });

        RemoveLayerCommand = new RelayCommand(parameter =>
        {
            if (parameter is LayerEditor layer)
            {
                Layers.Remove(layer);
                Revalidate();
            }
        });

        LoadFrom(profile);
    }

    public static string[] ControlNames { get; } =
        Enum.GetNames<ControllerControl>().Where(name => name != nameof(ControllerControl.None)).ToArray();

    public static string[] GestureNames { get; } = Enum.GetNames<InputGesture>();

    public static string[] CommandNames { get; } = Enum.GetNames<AgentCommandKind>();

    public static string[] ActivationNames { get; } = Enum.GetNames<LayerActivation>();

    public ObservableCollection<BindingEditor> Bindings { get; } = [];

    public ObservableCollection<LayerEditor> Layers { get; } = [];

    public ObservableCollection<string> Errors { get; } = [];

    public ICommand AddCommand { get; }

    public ICommand RemoveCommand { get; }

    public ICommand AddLayerCommand { get; }

    public ICommand RemoveLayerCommand { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (Set(ref _name, value))
            {
                Revalidate();
            }
        }
    }

    public BindingEditor? SelectedBinding
    {
        get => _selectedBinding;
        set => Set(ref _selectedBinding, value);
    }

    public bool IsValid
    {
        get => _isValid;
        private set => Set(ref _isValid, value);
    }

    public void LoadFrom(ControllerProfile profile)
    {
        Bindings.Clear();
        Layers.Clear();
        _name = profile.Name;
        Raise(nameof(Name));

        foreach (var layer in profile.Layers ?? [])
        {
            Layers.Add(new LayerEditor
            {
                Owner = this,
                Name = layer.Name,
                Activation = layer.Activation.ToString(),
            });
        }

        foreach (var binding in profile.Bindings)
        {
            Bindings.Add(new BindingEditor
            {
                Owner = this,
                Control = binding.Control.ToString(),
                Gesture = binding.Gesture.ToString(),
                Command = binding.Command.ToString(),
                ModifiersText = binding.Modifiers is { Count: > 0 }
                    ? string.Join("+", binding.Modifiers.OrderBy(modifier => modifier))
                    : string.Empty,
                HoldMilliseconds = binding.HoldDuration is { } hold
                    ? ((int)hold.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                DoublePressMilliseconds = binding.DoublePressWindow is { } window
                    ? ((int)window.TotalMilliseconds).ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                MinimumValue = binding.Gesture == InputGesture.AxisThreshold
                    ? binding.MinimumValue.ToString(CultureInfo.InvariantCulture)
                    : string.Empty,
                Text = binding.Text ?? string.Empty,
                RequiresPendingApproval = binding.RequiresPendingApproval,
                Layer = binding.Layer ?? string.Empty,
            });
        }

        SelectedBinding = Bindings.FirstOrDefault();
        Revalidate();
    }

    public bool TryBuildProfile(out ControllerProfile? profile)
    {
        var errors = new List<string>();
        var bindings = new List<InputBinding>();

        for (var index = 0; index < Bindings.Count; index++)
        {
            var binding = Bindings[index].ToBinding(index + 1, errors);
            if (binding is not null)
            {
                bindings.Add(binding);
            }
        }

        var layers = Layers.Count > 0
            ? Layers.Select(layer => layer.ToLayer()).ToList()
            : null;
        var candidate = new ControllerProfile(_name, bindings, layers);
        if (errors.Count == 0)
        {
            errors.AddRange(ControllerProfileValidator.Validate(candidate));
        }

        ReplaceErrors(errors);
        profile = errors.Count == 0 ? candidate : null;
        return profile is not null;
    }

    public void Revalidate() => TryBuildProfile(out _);

    public void ShowExternalErrors(IEnumerable<string> errors) => ReplaceErrors(errors.ToList());

    private void ReplaceErrors(List<string> errors)
    {
        Errors.Clear();
        foreach (var error in errors)
        {
            Errors.Add(error);
        }

        IsValid = errors.Count == 0 && Bindings.Count > 0;
    }
}
