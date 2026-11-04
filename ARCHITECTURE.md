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

## Robot communication

ESP32 and the macOS simulator share the versioned JSON protocol on `/ws/robot`. The WebSocket handler validates device messages, serializes outbound frames through one writer, and monitors monotonic connection and command deadlines. The in-memory registry coordinates active connections, telemetry, command transitions, and bounded command history across WebSocket and REST requests.

`/api/robots` exposes connected devices and accepts validated commands. Emergency stop preempts queued traffic and blocks new motion until confirmed. Robot communication is independent of the AI and voice pipelines. Devices enforce physical safety locally. See the [protocol reference](docs/ROBOT_PROTOCOL.md).

## macOS simulator

The native SwiftUI app runs on macOS 14+. Manual controls submit commands through the same REST endpoints as other P4 clients. A single WebSocket reader stays active during motion, and one writer orders registration, heartbeat, telemetry, and command transitions. The device stops locally when the socket closes or the peer stops answering WebSocket pings. Reconnection is user-initiated.

A deterministic differential-drive model uses monotonic elapsed time to integrate translation and in-place rotation and clamp travel to the requested target. The dashboard reads the model's pose and wheel travel through a Canvas. Face animation uses SwiftUI independently of motion. The app sends telemetry on state changes and at 8 Hz by default while moving. On emergency stop, it halts immediately and confirms the stop. P4 already records the interrupted movement as cancelled.

## AI robot commands

`RobotCommandInterpreter` sends each Czech or English instruction to the P2 provider as a separate JSON-mode request. It accepts up to five actions and validates the entire plan with the P4 command parser and configured limits before execution. The supported commands are `move_forward`, `move_backward`, `rotate_left`, `rotate_right`, `set_expression`, and a final or standalone `stop`.

A reservation binds each plan to its current P4 connection from interpretation onward. The hosted executor tracks accepted plans in memory and submits one action at a time through P4. It advances only after a device-confirmed completion. Failure, timeout, disconnect, or cancellation ends the sequence. The registry cancels the reservation atomically with external emergency stop, so even a stop between actions prevents further submission. P4 retains ownership of dispatch, deadlines, and stop priority. Movement is never retried.

The AI endpoints use the same trusted-network boundary as P4. Automated coverage substitutes the AI provider and uses in-process WebSocket peers. P6-B will verify live Czech and English interpretation, rejection behavior, and stop during motion with local Qwen and the macOS simulator.
