using System.Text.Json;
using System.Text.Json.Serialization;

namespace CtrlAgent.Core;

/// <summary>
/// Validates that a profile is unambiguous and that approval-capable bindings
/// are configured deliberately, per the project safety rules.
/// </summary>
public static class ControllerProfileValidator
{
    public static IReadOnlyList<string> Validate(ControllerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            errors.Add("Profile name must not be empty.");
        }

        if (profile.Bindings.Count == 0)
        {
            errors.Add("Profile must contain at least one binding.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var gesturesByChord = new Dictionary<string, HashSet<InputGesture>>(StringComparer.Ordinal);

        foreach (var binding in profile.Bindings)
        {
            var chord = Describe(binding);

            if (!seen.Add($"{chord}|{binding.Gesture}"))
            {
                errors.Add($"Duplicate {binding.Gesture} binding on '{chord}'.");
            }

            if (!gesturesByChord.TryGetValue(chord, out var gestures))
            {
                gestures = [];
                gesturesByChord[chord] = gestures;
            }

            gestures.Add(binding.Gesture);

            if (binding.Gesture == InputGesture.AxisThreshold &&
                (binding.MinimumValue <= 0f || binding.MinimumValue > 1f))
            {
                errors.Add($"AxisThreshold binding on '{chord}' needs a minimumValue between 0 and 1.");
            }

            if (binding.HoldDuration is { } hold && hold <= TimeSpan.Zero)
            {
                errors.Add($"Binding on '{chord}' has a non-positive hold duration.");
            }

            if (binding.DoublePressWindow is { } window && window <= TimeSpan.Zero)
            {
                errors.Add($"Binding on '{chord}' has a non-positive double-press window.");
            }

            ValidateApprovalSafety(binding, chord, errors);
        }

        foreach (var (chord, gestures) in gesturesByChord)
        {
            // Press fires immediately on the way down, so combining it with a
            // release-resolved or double-press gesture on the same chord would
            // trigger two commands from one physical action.
            if (gestures.Contains(InputGesture.Press) &&
                (gestures.Contains(InputGesture.Tap) ||
                 gestures.Contains(InputGesture.Hold) ||
                 gestures.Contains(InputGesture.DoublePress)))
            {
                errors.Add($"'{chord}' mixes Press with Tap/Hold/DoublePress; use Tap instead of Press.");
            }

            // The first tap of a double-press would already fire the Tap binding.
            if (gestures.Contains(InputGesture.Tap) && gestures.Contains(InputGesture.DoublePress))
            {
                errors.Add($"'{chord}' mixes Tap with DoublePress; the first tap would fire both.");
            }
        }

        return errors;
    }

    private static void ValidateApprovalSafety(InputBinding binding, string chord, List<string> errors)
    {
        var isApprovalFamily = binding.Command is
            AgentCommandKind.ApproveOnce or
            AgentCommandKind.ApproveForSession or
            AgentCommandKind.Decline;

        if (!isApprovalFamily)
        {
            return;
        }

        if (!binding.RequiresPendingApproval)
        {
            errors.Add($"{binding.Command} binding on '{chord}' must set requiresPendingApproval.");
        }

        var isApproval = binding.Command is
            AgentCommandKind.ApproveOnce or
            AgentCommandKind.ApproveForSession;

        if (isApproval && !IsDeliberate(binding))
        {
            errors.Add(
                $"{binding.Command} binding on '{chord}' must use a paddle, a modifier chord, or a hold gesture.");
        }
    }

    private static bool IsDeliberate(InputBinding binding) =>
        IsPaddle(binding.Control) ||
        binding.Modifiers is { Count: > 0 } ||
        binding.Gesture == InputGesture.Hold;

    private static bool IsPaddle(ControllerControl control) => control is
        ControllerControl.PaddleLeft1 or
        ControllerControl.PaddleLeft2 or
        ControllerControl.PaddleRight1 or
        ControllerControl.PaddleRight2;

    private static string Describe(InputBinding binding) =>
        binding.Modifiers is { Count: > 0 }
            ? string.Join("+", binding.Modifiers.OrderBy(modifier => modifier)) + "+" + binding.Control
            : binding.Control.ToString();
}

/// <summary>
/// Versioned JSON persistence for controller profiles. Deserialization
/// validates the profile and throws <see cref="FormatException"/> with every
/// problem found, so an unsafe or ambiguous profile can never load.
/// </summary>
public static class ControllerProfileJson
{
    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static string Serialize(ControllerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var document = new ProfileDocument
        {
            Version = CurrentVersion,
            Name = profile.Name,
            Bindings = profile.Bindings.Select(binding => new BindingDocument
            {
                Control = ToCamel(binding.Control.ToString()),
                Gesture = ToCamel(binding.Gesture.ToString()),
                Command = ToCamel(binding.Command.ToString()),
                Modifiers = binding.Modifiers is { Count: > 0 }
                    ? binding.Modifiers.OrderBy(modifier => modifier).Select(modifier => ToCamel(modifier.ToString())).ToList()
                    : null,
                MinimumValue = binding.Gesture == InputGesture.AxisThreshold ? binding.MinimumValue : null,
                Text = binding.Text,
                RequiresPendingApproval = binding.RequiresPendingApproval ? true : null,
                HoldMilliseconds = binding.HoldDuration is { } hold ? (int)hold.TotalMilliseconds : null,
                DoublePressMilliseconds = binding.DoublePressWindow is { } window ? (int)window.TotalMilliseconds : null,
            }).ToList(),
        };

        return JsonSerializer.Serialize(document, Options);
    }

    public static ControllerProfile Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        ProfileDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<ProfileDocument>(json, Options);
        }
        catch (JsonException exception)
        {
            throw new FormatException($"Profile JSON is malformed: {exception.Message}", exception);
        }

        if (document is null)
        {
            throw new FormatException("Profile JSON is empty.");
        }

        var errors = new List<string>();

        if (document.Version is not CurrentVersion)
        {
            errors.Add($"Unsupported profile version '{document.Version?.ToString() ?? "missing"}'; expected {CurrentVersion}.");
        }

        var bindings = new List<InputBinding>();
        var documents = document.Bindings ?? [];

        for (var index = 0; index < documents.Count; index++)
        {
            var binding = ParseBinding(documents[index], index + 1, errors);
            if (binding is not null)
            {
                bindings.Add(binding);
            }
        }

        var profile = new ControllerProfile(document.Name ?? string.Empty, bindings);

        if (errors.Count == 0)
        {
            errors.AddRange(ControllerProfileValidator.Validate(profile));
        }

        if (errors.Count > 0)
        {
            throw new FormatException("Invalid profile: " + string.Join(" ", errors));
        }

        return profile;
    }

    private static InputBinding? ParseBinding(BindingDocument document, int position, List<string> errors)
    {
        var valid = true;

        if (!Enum.TryParse<ControllerControl>(document.Control, ignoreCase: true, out var control) ||
            control == ControllerControl.None)
        {
            errors.Add($"Binding {position} has an unknown control '{document.Control}'.");
            valid = false;
        }

        var gesture = InputGesture.Press;
        if (document.Gesture is not null &&
            !Enum.TryParse(document.Gesture, ignoreCase: true, out gesture))
        {
            errors.Add($"Binding {position} has an unknown gesture '{document.Gesture}'.");
            valid = false;
        }

        if (!Enum.TryParse<AgentCommandKind>(document.Command, ignoreCase: true, out var command))
        {
            errors.Add($"Binding {position} has an unknown command '{document.Command}'.");
            valid = false;
        }

        HashSet<ControllerControl>? modifiers = null;
        if (document.Modifiers is { Count: > 0 })
        {
            modifiers = [];
            foreach (var name in document.Modifiers)
            {
                if (Enum.TryParse<ControllerControl>(name, ignoreCase: true, out var modifier) &&
                    modifier != ControllerControl.None)
                {
                    modifiers.Add(modifier);
                }
                else
                {
                    errors.Add($"Binding {position} has an unknown modifier '{name}'.");
                    valid = false;
                }
            }
        }

        if (document.HoldMilliseconds is <= 0)
        {
            errors.Add($"Binding {position} needs a positive holdMilliseconds.");
            valid = false;
        }

        if (document.DoublePressMilliseconds is <= 0)
        {
            errors.Add($"Binding {position} needs a positive doublePressMilliseconds.");
            valid = false;
        }

        if (!valid)
        {
            return null;
        }

        return new InputBinding(
            control,
            gesture,
            command,
            modifiers,
            document.MinimumValue ?? 0.5f,
            document.Text,
            document.RequiresPendingApproval ?? false,
            document.HoldMilliseconds is { } hold ? TimeSpan.FromMilliseconds(hold) : null,
            document.DoublePressMilliseconds is { } window ? TimeSpan.FromMilliseconds(window) : null);
    }

    private static string ToCamel(string name) =>
        char.ToLowerInvariant(name[0]) + name[1..];

    private sealed class ProfileDocument
    {
        public int? Version { get; set; }

        public string? Name { get; set; }

        public List<BindingDocument>? Bindings { get; set; }
    }

    private sealed class BindingDocument
    {
        public string? Control { get; set; }

        public string? Gesture { get; set; }

        public string? Command { get; set; }

        public List<string>? Modifiers { get; set; }

        public float? MinimumValue { get; set; }

        public string? Text { get; set; }

        public bool? RequiresPendingApproval { get; set; }

        public int? HoldMilliseconds { get; set; }

        public int? DoublePressMilliseconds { get; set; }
    }
}
