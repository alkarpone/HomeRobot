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
    │    ├── Ollama / robot-ai
    │    └── Cloud AI (future)
    ├── Text-to-Speech
    ├── Robot Controller
    ├── Conversation and Memory (SQLite)
    └── Device Communication
```

The server exposes REST health and AI endpoints and a JSON WebSocket endpoint. The diagram includes planned components.

- ESP32 handles hardware and device communication. The server host handles speech-to-text and text-to-speech. In the macOS development setup, the Windows PC runs Ollama and the `robot-ai` model.
- The server uses Ollama’s HTTP API at the configured base URL. AI providers should be replaceable. OpenClaw is an optional future integration.
- The server must validate motor commands. ESP32 firmware must independently enforce safety limits.
- Keep hardware-specific logic separate from AI logic.

## Text conversation

`POST /api/ai/chat` serializes turns per session. `ConversationPrompt` combines fixed safety and capability rules with the configured personality, quotes up to three relevant SQLite records in a separate user-data message, and appends recent turns and the current request. Ollama uses the existing non-streaming HTTP provider. Expression hints are optional and allowlisted. The chat pipeline has no device-dispatch capability. P6 continues to interpret robot actions independently and submits only validated plans to P4.

Each session retains complete user/assistant pairs up to `Conversation:MaxHistoryMessages` (20 by default) and 6,000 characters. Inactive sessions expire after 30 minutes. The process holds up to 256 sessions. Session IDs separate local and remote conversation history. Deterministic memory-operation replies stay separate from conversation history.

`MemoryStore` initializes a local SQLite file on first write. It stores up to 1,000 records of at most 500 characters, with category, timestamps and source metadata. SQLite filters by category and enforces normalized uniqueness. The retrieval interface scores Czech keywords and caps prompt results. Memory commands are parsed from user input before model generation. Individual mutations check the selected row's revision. Bulk conversation deletion requires a separate confirmation within two minutes and checks the selected set transactionally. The loopback boundary applies to management and conversational memory. See the [configuration and API reference](README.md#personality-and-memory).

`GET /api/ai/status` checks Ollama reachability and model availability with a three-second deadline.

## Voice conversation

The Python push-to-talk client records and plays audio through PortAudio. ASP.NET Core accepts bounded multipart WAV uploads and validates PCM headers, duration, and size before calling `ISpeechToTextService`. Digital silence is handled directly and faster-whisper applies VAD to other input.

The Whisper adapter starts a persistent Python worker on the first non-silent request. The worker loads a local CTranslate2 model once and exchanges newline-delimited Base64 WAV and JSON over private process pipes. One transcription runs at a time. Cancellation or failure during an exchange resets the worker so a later request starts with a clean protocol state.

Voice chat passes the transcription to the same `ConversationStore` and `IAIProvider` used by text chat. The per-session gate covers AI generation and synthesis, and the store commits model-generated history only after both succeed. Explicit memory writes commit before synthesis so their confirmations reflect the database result. Timings include provider queue time, and total processing includes WAV validation and Base64 encoding.

`ITextToSpeechService` isolates Piper behind a configurable executable. It uses a local voice model and per-call temporary UTF-8 text and WAV files, which are removed after processing. Piper invocations are serialized. Provider deadlines include queue time, and the adapters stop subprocesses on cancellation. Provider output is discarded or read through private pipes without logging audio or conversation text.

The macOS simulator records bounded mono PCM WAV files on demand and uses the transcription and synthesis endpoints. Whisper defaults to Czech and accepts a short configurable initial prompt. Transcription returns raw and normalized text, the detected intent, a route, and timings. Ordinary conversation uses the P2 session store and a short Czech reply prompt. The simulator opts in to memory handling on transcription, so explicit commands use the original Whisper confidence before routing. The simulator then synthesizes the returned Czech reply.

`RobotVocabulary` groups Czech phrases and known transcription errors separately from executable commands. Add recognition-only phrases under their intent and classify a new unsupported group in `VoiceIntent`. Add a voice action only after P4 can validate it and the device advertises its capability. Extend the strict grammar and tests before enabling dispatch. The full vocabulary stays on the server.

`VoiceIntent` derives ordered actions from supported Czech phrasing, requiring a direction and quantity with its unit. An omitted speed defaults to 30 percent, capped by the configured maximum. Unsupported, negated, unclear, and incomplete plans receive no authorization. For complete plans, a short-lived, one-use instruction ID binds the exact raw transcript to the current robot connection. The command endpoint submits those actions directly to the plan executor after checking the entire plan against P4 limits and device capabilities. The voice path applies known transcription corrections locally and does not call Ollama to interpret commands.

A clear standalone stop phrase with sufficient Whisper confidence goes directly through P4. Manual stop remains available while recording or processing. P4 stop invalidates pending voice instruction IDs and cancels active plans under the registry lock. The app cancels its voice task, recording, and playback independently of the manual stop response. It polls P6 status and only speaks completion after the simulator confirms it.

## Robot communication

ESP32 and the macOS simulator share the versioned JSON protocol on `/ws/robot`. The WebSocket handler validates device messages, serializes outbound frames through one writer, and monitors monotonic connection and command deadlines. The in-memory registry coordinates active connections, telemetry, command transitions, and bounded command history across WebSocket and REST requests.

`/api/robots` exposes connected devices and accepts validated commands. Emergency stop preempts queued traffic and blocks new motion until confirmed. Robot communication is independent of the AI and voice pipelines. Devices enforce physical safety locally. See the [protocol reference](docs/ROBOT_PROTOCOL.md).

## ESP32-S3 camera firmware

The standalone ESP-IDF project in `firmware/esp32-s3` brings up the OV5640 over
the carrier's parallel camera bus. The sensor encodes VGA JPEGs and the firmware
keeps two frame buffers in PSRAM. On the trusted development LAN, the board
serves snapshots, one concurrent MJPEG stream, health, and hardware information
over HTTP. An asynchronous stream worker leaves the HTTP task available for
the other routes.

The Mac provisions Wi-Fi credentials into a dedicated NVS partition over USB.
The ESP32-S3 joins the LAN as a station and logs its DHCP address. This camera
bring-up is independent of HomeRobot.Server and the P4 WebSocket protocol.

The OLED task scans and drives I2C0 on GPIO1 (SDA) and GPIO2 (SCK) while
Wi-Fi and HTTP start. It updates the SH1106 face in a loop and releases I2C0
if a transfer fails. The OV5640 uses I2C1 for SCCB on GPIO4 and GPIO5.

## ESP32-S3 speaker output

The standalone SpeakerManager component initializes Philips I2S TX independently
of camera and OLED. It shares the camera HTTP server on the trusted LAN. An
asynchronous worker reserves the speaker through upload and DMA playback. It
buffers one PCM clip of up to 30 seconds in PSRAM, applies per-clip volume, and
copies mono samples to both I2S slots. A competing request receives HTTP 409.
See the [wiring and HTTP contract](HARDWARE.md).

HomeRobot.Server reuses `Esp32SpeakerClient`'s WAV parsing, downmixing and conversion
into 16 kHz mono PCM16 little-endian. P13-A sends that PCM through short-lived,
command-scoped HTTPS resources held by `AudioPlaybackService`. The original WAV
returns to the Mac from the same synthesis. Both outputs use one Piper call.
The existing direct HTTP speaker client and firmware upload routes remain available.

An optional ESP32 WSS client registers through the existing P4 registry and heartbeat
with audio-only capabilities. `audio_play` and targeted `audio_stop` use the existing
command/ack/result envelopes, with a separate 45-second audio deadline. The device
streams fixed-length authenticated downloads into SpeakerManager's existing DMA/I2S
write path in bounded blocks. A separate control task keeps heartbeats and cancellation
responsive. Disconnect cancels playback and reconnect starts a fresh session.

The simulator persists output toggles and device selection, polls P4 audio status,
and routes text, microphone and Vision replies through its existing synthesis/player.
Mac errors and remote errors are independent. Pending playback IDs let a stop suppress
late delivery during synthesis. P10 exposes physical speaker diagnostics and a Piper
test. See [setup, limits and hardware verification](docs/ESP32_TTS.md).

## ESP32-S3 microphone input

The microphone uses I2S1 RX independently of speaker TX and camera capture.
One asynchronous HTTP worker acquires mono samples on demand, converts them
to signed 16 kHz PCM16 and reports signal statistics or returns a five-second
WAV buffered in PSRAM. The WAV matches the server's existing transcription
input format. See the [wiring and HTTP contract](HARDWARE.md#esp32-s3-i2s-mems-microphone).

## macOS simulator

The native SwiftUI app runs on macOS 14+. Manual controls submit commands through the same REST endpoints as other P4 clients. A single WebSocket reader stays active during motion, and one writer orders registration, heartbeat, telemetry, and command transitions. The device stops locally when the socket closes or the peer stops answering WebSocket pings. Reconnection is user-initiated.

A deterministic differential-drive model uses monotonic elapsed time to integrate translation and in-place rotation and clamp travel to the requested target. The dashboard reads the model's pose and wheel travel through a Canvas. Face animation uses SwiftUI independently of motion. The app sends telemetry on state changes and at 8 Hz by default while moving. On emergency stop, it halts immediately and confirms the stop. P4 already records the interrupted movement as cancelled.

The face samples a speech envelope from the existing voice player's `currentTime` on its SwiftUI animation timeline (up to 30 updates per second). Before playback, the simulator reads the Piper PCM WAV once off the main actor. It computes RMS over approximately 20 ms windows, applies a configurable noise gate and attack/release smoothing, then interpolates mouth openness between windows. Playback uses the original WAV data. The mouth closes when the player stops, and completion or cancellation clears the envelope. The amplitude mapping approximates speech rhythm. It does not distinguish phonemes. Unsupported envelope formats can still play with a neutral mouth if AVAudioPlayer accepts them.

Temporary voice states take precedence over movement, and movement takes precedence over the expression last set by P4. When speech ends, the face restores the conversational hint when available. The next voice turn or successful P4 expression clears the hint. Executing is a local face state and the P4 expression vocabulary stays the same.

Robot Settings uses the dashboard's server address for direct loopback HTTP requests, independently of robot registration. SwiftUI keeps drafts and fetched records in memory only; SQLite and the server profile remain authoritative. Category filtering runs on the server; text search runs on the loaded records. Diagnostics show the last API check and loaded record count rather than database metrics.

`POST /api/personality/preview` accepts a draft profile and sample question, validates both, and calls the existing provider with the standard safety prompt and an isolated turn. It does not consult or update conversation history or SQLite, invoke speech services, or dispatch device commands.

## AI robot commands

`RobotCommandInterpreter` sends each Czech or English instruction to the P2 provider as a separate JSON-mode request. It accepts up to five actions and validates the entire plan with the P4 command parser and configured limits before execution. It also checks each movement’s distance or angle against the numeric quantities and units in the instruction, in order. The supported commands are `move_forward`, `move_backward`, `rotate_left`, `rotate_right`, `set_expression`, and a final or standalone `stop`.

A reservation binds each plan to its current P4 connection from interpretation onward. The hosted executor tracks accepted plans in memory and submits one action at a time through P4. It advances only after a device-confirmed completion. Failure, timeout, disconnect, or cancellation ends the sequence. The registry cancels the reservation atomically with external emergency stop, so even a stop between actions prevents further submission. P4 retains ownership of dispatch, deadlines, and stop priority. Movement is never retried.

The AI endpoints use the same trusted-network boundary as P4. The simulator submits instructions to the server’s AI REST endpoint and polls the plan while the device connection receives P4 commands. Its Stop button calls P4 directly. The .NET tests use simulated AI output and in-process WebSocket peers. The opt-in macOS integration test uses the configured Ollama endpoint.

## Still-image vision

The macOS simulator routes normalized Whisper vision intents through the existing voice turn. A fresh request starts the selected camera if needed, captures one still through the shared AVFoundation session, and speaks the Czech answer with the existing Piper player and face animation. Historical questions require the previous image. A descriptive follow-up uses it while available and captures a fresh still if it has expired. Manual still capture and ImageIO import remain available.

The loopback Vision API accepts one analysis at a time, validates the image, and sends one JPEG to the existing Ollama provider. The prompt uses the P8 personality and may quote the latest local conversation pair to resolve references. The provider receives explicit cached-image instructions and the prior visual answer for a follow-up. Vision does not write conversation history or SQLite memory and has no device dispatch path.

The server retains at most eight normalized stills in RAM, one per opaque visual session. Each entry records its capture time and initial question and answer. Expiry is measured from capture (default 120 seconds, configurable via `Vision:ContextLifetimeSeconds`). Follow-ups leave expiry unchanged. The simulator clears its server context on clear or close, and the server also removes expired entries.

Auto Scan starts disabled. The panel owns the periodic task and cancels it when disabled or closed. Voice work pauses scans, busy ticks are dropped, and scan results use a separate, stateless Vision request without speech or actions. The app compares 32 × 32 grayscale samples against its last successful scan with compensation for small global lighting shifts and pixel noise. It sends a new JPEG only when warranted by the threshold or change detection is off. Samples stay in RAM and the visible camera indicator remains on while preview is running.

Future ESP32 support can implement a still-image source with explicit user capture, capability discovery and authenticated, bounded delivery. Feed its JPEG/PNG into the same validation and inference path. The local API boundary stays in place until a separate authenticated hardware transport is defined. P4 WebSocket v1 is unchanged and carries no image bytes.


## Web dashboard

HomeRobot.Server serves a dependency-free dashboard from `wwwroot`. Per-view polling uses
the existing REST APIs for personality, SQLite memory, voice synthesis and P4 robot commands.
Polling stops when a view is left and skips hidden tabs. System endpoints expose sanitized
configuration, cached Ollama checks, a Whisper worker probe and process-local diagnostics.
Robot history uses the bounded P4 registry, and an Emergency Stop follows the existing P4
validation, preemption and acknowledgement path.

Validated allowlisted overrides are atomically saved to a separate JSON file. Its startup
configuration source sits between appsettings and deployment providers. The server uses a
startup snapshot, and the API exposes saved and effective values separately. Personality and
memory keep their existing live SQLite paths. A bounded logging provider records only
reviewed templates with fixed display messages. It discards structured values, exceptions,
request content and inference payloads.

Access middleware applies before static files, REST and WebSockets. The default bind is
loopback. Nonlocal traffic requires explicit opt-in, direct HTTPS and single-admin HTTP Basic
authentication against a configured digest. Origin and Fetch Metadata checks protect browser
requests, and simple cross-origin forms cannot submit P4 robot commands. The loopback-only
Vision ingestion and conversational memory checks remain separate from dashboard access.
