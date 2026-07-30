using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CtrlAgent.Gui;

public enum SpeechProviderKind
{
    OpenAi,
    LocalWhisper,
    Windows,
}

public enum SpeechResultKind
{
    Recognized,
    NoSpeech,
    Cancelled,
    TimedOut,
    Unavailable,
    CaptureFailed,
    TranscriptionFailed,
}

public sealed record SpeechTranscriptionResult(
    SpeechResultKind Kind,
    string? Text = null,
    string? Message = null)
{
    public bool Succeeded => Kind == SpeechResultKind.Recognized && !string.IsNullOrWhiteSpace(Text);
}

public sealed record SpeechProviderOptions(
    string? Language,
    string? Vocabulary,
    TimeSpan Timeout);

public interface ISpeechToTextProvider : IAsyncDisposable
{
    SpeechProviderKind Kind { get; }
    string DisplayName { get; }
    Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken);
    Task<SpeechTranscriptionResult> TranscribeAsync(
        byte[] waveAudio,
        SpeechProviderOptions options,
        CancellationToken cancellationToken);
}

public static class SpeechProviderSettings
{
    public static SpeechProviderKind Provider { get; set; } = SpeechProviderKind.Windows;
    public static string OpenAiModel { get; set; } = "gpt-4o-transcribe";
    public static string? WhisperExecutable { get; set; }
    public static string? WhisperModel { get; set; }

    public static string ProviderLabel => Provider switch
    {
        SpeechProviderKind.OpenAi => "OpenAI — best accuracy",
        SpeechProviderKind.LocalWhisper => "Local Whisper — private",
        _ => "Windows Speech — basic",
    };

    public static SpeechProviderKind NextProvider() => Provider switch
    {
        SpeechProviderKind.Windows => SpeechProviderKind.OpenAi,
        SpeechProviderKind.OpenAi => SpeechProviderKind.LocalWhisper,
        _ => SpeechProviderKind.Windows,
    };
}

/// <summary>Stores the OpenAI API key encrypted for the current Windows user.
/// The key never enters settings.json, logs, or release artifacts.</summary>
public static class OpenAiKeyStore
{
    private static string KeyPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "CtrlAgent", "openai-key.bin");

    public static string? Get()
    {
        var environment = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrWhiteSpace(environment))
        {
            return environment.Trim();
        }

        try
        {
            if (!OperatingSystem.IsWindows() || !File.Exists(KeyPath))
            {
                return null;
            }

            var protectedBytes = File.ReadAllBytes(KeyPath);
            var clear = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(clear);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Secure API-key storage requires Windows DPAPI.");
        }

        var clear = Encoding.UTF8.GetBytes(key.Trim());
        var protectedBytes = ProtectedData.Protect(clear, null, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, protectedBytes);
    }

    public static void Remove()
    {
        try { File.Delete(KeyPath); } catch (Exception) { }
    }
}

public sealed class OpenAiSpeechToTextProvider : ISpeechToTextProvider
{
    private readonly HttpClient _httpClient;

    public OpenAiSpeechToTextProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public SpeechProviderKind Kind => SpeechProviderKind.OpenAi;
    public string DisplayName => "OpenAI transcription";

    public Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken) =>
        Task.FromResult(OpenAiKeyStore.Get() is null
            ? "OpenAI needs an API key. Add one in Voice settings or set OPENAI_API_KEY."
            : null);

    public async Task<SpeechTranscriptionResult> TranscribeAsync(
        byte[] waveAudio,
        SpeechProviderOptions options,
        CancellationToken cancellationToken)
    {
        var key = OpenAiKeyStore.Get();
        if (string.IsNullOrWhiteSpace(key))
        {
            return new(SpeechResultKind.Unavailable, Message: "OpenAI API key is missing.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/audio/transcriptions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(waveAudio);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "ctrlagent-dictation.wav");
        form.Add(new StringContent(SpeechProviderSettings.OpenAiModel), "model");
        form.Add(new StringContent("json"), "response_format");
        if (!string.IsNullOrWhiteSpace(options.Language))
        {
            form.Add(new StringContent(options.Language), "language");
        }
        if (!string.IsNullOrWhiteSpace(options.Vocabulary))
        {
            form.Add(new StringContent(options.Vocabulary), "prompt");
        }
        request.Content = form;

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return new(SpeechResultKind.TranscriptionFailed,
                    Message: $"OpenAI transcription failed ({(int)response.StatusCode}): {SafeApiError(body)}");
            }

            using var json = JsonDocument.Parse(body);
            var text = json.RootElement.TryGetProperty("text", out var property) ? property.GetString() : null;
            return string.IsNullOrWhiteSpace(text)
                ? new(SpeechResultKind.NoSpeech, Message: "OpenAI returned no speech.")
                : new(SpeechResultKind.Recognized, text.Trim());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(SpeechResultKind.Cancelled, Message: "Transcription was cancelled.");
        }
        catch (Exception exception)
        {
            return new(SpeechResultKind.TranscriptionFailed, Message: exception.Message);
        }
    }

    private static string SafeApiError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.GetProperty("error").GetProperty("message").GetString() ?? "Unknown API error.";
        }
        catch (Exception)
        {
            return body.Length <= 300 ? body : body[..300];
        }
    }

    public ValueTask DisposeAsync()
    {
        _httpClient.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>Runs a local whisper.cpp-compatible executable. The executable must
/// accept: -m model -f input.wav -otxt -of output-base.</summary>
public sealed class LocalWhisperSpeechToTextProvider : ISpeechToTextProvider
{
    public SpeechProviderKind Kind => SpeechProviderKind.LocalWhisper;
    public string DisplayName => "Local Whisper";

    public Task<string?> CheckAvailabilityAsync(CancellationToken cancellationToken)
    {
        var executable = SpeechProviderSettings.WhisperExecutable;
        var model = SpeechProviderSettings.WhisperModel;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            return Task.FromResult<string?>("Choose a whisper.cpp executable in Voice settings.");
        }
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model))
        {
            return Task.FromResult<string?>("Choose a downloaded Whisper model in Voice settings.");
        }
        return Task.FromResult<string?>(null);
    }

    public async Task<SpeechTranscriptionResult> TranscribeAsync(
        byte[] waveAudio,
        SpeechProviderOptions options,
        CancellationToken cancellationToken)
    {
        var unavailable = await CheckAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (unavailable is not null)
        {
            return new(SpeechResultKind.Unavailable, Message: unavailable);
        }

        var root = Path.Combine(Path.GetTempPath(), "CtrlAgent-whisper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var input = Path.Combine(root, "input.wav");
        var outputBase = Path.Combine(root, "result");
        await File.WriteAllBytesAsync(input, waveAudio, cancellationToken).ConfigureAwait(false);

        try
        {
            var start = new ProcessStartInfo
            {
                FileName = SpeechProviderSettings.WhisperExecutable!,
                WorkingDirectory = root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            start.ArgumentList.Add("-m"); start.ArgumentList.Add(SpeechProviderSettings.WhisperModel!);
            start.ArgumentList.Add("-f"); start.ArgumentList.Add(input);
            start.ArgumentList.Add("-otxt");
            start.ArgumentList.Add("-of"); start.ArgumentList.Add(outputBase);
            if (!string.IsNullOrWhiteSpace(options.Language))
            {
                start.ArgumentList.Add("-l"); start.ArgumentList.Add(options.Language);
            }

            using var process = Process.Start(start);
            if (process is null)
            {
                return new(SpeechResultKind.TranscriptionFailed, Message: "Could not start local Whisper.");
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return new(SpeechResultKind.TranscriptionFailed, Message: $"Local Whisper exited with {process.ExitCode}: {error.Trim()}");
            }

            var output = outputBase + ".txt";
            var text = File.Exists(output)
                ? await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            return string.IsNullOrWhiteSpace(text)
                ? new(SpeechResultKind.NoSpeech, Message: "Local Whisper returned no speech.")
                : new(SpeechResultKind.Recognized, text.Trim());
        }
        catch (OperationCanceledException)
        {
            return new(SpeechResultKind.Cancelled, Message: "Local transcription was cancelled.");
        }
        catch (Exception exception)
        {
            return new(SpeechResultKind.TranscriptionFailed, Message: exception.Message);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (Exception) { }
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
