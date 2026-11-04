# Architecture

```text
ESP32 Robot
    |
    | Wi-Fi / WebSocket
    |
HomeRobot.Server (.NET)
    |
    ├── Audio Input
    ├── Speech-to-Text
    ├── AI Router
    │    ├── Ollama / Qwen
    │    └── Cloud AI (future)
    ├── Text-to-Speech
    ├── Robot Controller
    ├── Memory Service
    └── Device Communication
```

The server exposes REST health and AI endpoints and a JSON WebSocket endpoint. The diagram includes planned components.

- ESP32 handles hardware and device communication. The PC handles AI processing, speech-to-text, and text-to-speech.
- Ollama uses its local HTTP API. AI providers should be replaceable. OpenClaw is an optional future integration.
- The server must validate motor commands. ESP32 firmware must independently enforce safety limits.
- Keep hardware-specific logic separate from AI logic.

## Text conversation

`POST /api/ai/chat` validates the user message and resolves an in-memory session. Requests for a session run serially so each successful turn sees the preceding turns. The conversation store prepends the system prompt and passes the conversation to `IAIProvider`. The Ollama implementation uses an injected `HttpClient` and calls `POST /api/chat` with streaming disabled. Only successful turns update the history.

The store retains the latest 20 user and assistant messages per session plus the shared system prompt. Sessions expire after 30 minutes of inactivity and are purged on the next chat request. The store holds up to 256 sessions per server process.

`GET /api/ai/status` calls Ollama's `GET /api/tags` with a three-second deadline to report reachability and configured model availability.

Future work includes audio streaming, camera processing, display commands, motor control, database storage, a web dashboard, and authentication.

## Voice conversation

The Python push-to-talk client records and plays audio through PortAudio. ASP.NET Core accepts bounded multipart WAV uploads and validates PCM headers, duration, and size before calling `ISpeechToTextService`. Digital silence is handled directly and faster-whisper applies VAD to other input.

The Whisper adapter starts a persistent Python worker on the first non-silent request. The worker loads a local CTranslate2 model once and exchanges newline-delimited Base64 WAV and JSON over private process pipes. One transcription runs at a time. Cancellation or failure during an exchange resets the worker so a later request starts with a clean protocol state.

Voice chat passes the transcription to the same `ConversationStore` and `IAIProvider` used by text chat. The per-session gate covers AI generation and synthesis, and the store commits a voice turn only after both succeed. Timings include provider queue time, and total processing includes WAV validation and Base64 encoding.

`ITextToSpeechService` isolates Piper behind a configurable executable. It uses a local voice model and per-call temporary UTF-8 text and WAV files, which are removed after processing. Piper invocations are serialized. Provider deadlines include queue time, and the adapters stop subprocesses on cancellation. Provider output is discarded or read through private pipes without logging audio or conversation text.
