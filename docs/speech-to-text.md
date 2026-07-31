# Speech-to-text providers

CtrlAgent supports three speech-to-text providers behind one recording pipeline.

| Provider | Accuracy | Privacy | Requirements |
|---|---|---|---|
| OpenAI | Best default | Audio is sent to OpenAI for transcription | Internet, API billing, API key |
| Local Whisper | High | Audio stays on the machine | whisper.cpp executable and model file |
| Windows Speech | Basic fallback | Local | Installed Windows speech language |

## Provider configuration

Speech preferences are stored in `%AppData%/CtrlAgent/settings.json`. The OpenAI key is deliberately excluded from that file.

```json
{
  "speechProvider": "OpenAi",
  "openAiSpeechModel": "gpt-4o-transcribe",
  "speechLanguage": "en-US"
}
```

Valid provider values are `OpenAi`, `LocalWhisper`, and `Windows`.

### OpenAI

An OpenAI API key and API billing are required. A ChatGPT subscription does not supply API credits.

CtrlAgent resolves the key in this order:

1. `OPENAI_API_KEY` environment variable;
2. the Windows-DPAPI encrypted key store managed by `OpenAiKeyStore`.

The key must never be placed in `settings.json`, source control, logs, screenshots, or issue reports.

The default model is `gpt-4o-transcribe`. The recorded WAV is held temporarily, uploaded for transcription, and discarded after the request.

### Local Whisper

CtrlAgent currently integrates with a whisper.cpp-compatible command-line executable. Configure:

```json
{
  "speechProvider": "LocalWhisper",
  "whisperExecutable": "C:\\Tools\\whisper.cpp\\whisper-cli.exe",
  "whisperModel": "C:\\Models\\ggml-large-v3-turbo.bin",
  "speechLanguage": "en"
}
```

The executable must support:

```text
-m <model> -f <input.wav> -otxt -of <output-base>
```

No audio leaves the computer. Model download and GPU/runtime installation are user-managed in this implementation; a future model manager may automate those steps.

### Windows Speech

Windows Speech uses the installed `System.Speech` desktop recognizer. It is the zero-account fallback, not the recommended accuracy option. Install a matching Windows speech language when the provider reports that no compatible recognizer exists.

## Reliability behavior

Each dictation attempt now:

1. opens the selected microphone;
2. records into a fresh bounded buffer;
3. closes the microphone;
4. passes a standard 16 kHz mono WAV to the selected provider;
5. applies a transcription timeout;
6. deletes temporary audio and transcript files.

This prevents stale audio from one attempt reaching the next and avoids keeping a microphone open for the lifetime of Mainframe.

Results distinguish recognized speech, no speech, cancellation, timeout, unavailable providers, capture failures, and transcription failures.

## Required validation

Before calling a provider supported, test the exact packaged build for:

- default microphone;
- explicitly selected microphone;
- changing the microphone after first use;
- silence;
- cancellation;
- repeated attempts;
- unplug and reconnect;
- disabled Windows microphone privacy permission;
- missing API key;
- invalid API key;
- network failure;
- missing local executable/model;
- Windows recognizer language mismatch.

Do not include API keys or recorded audio in validation evidence.