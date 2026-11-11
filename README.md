# HomeRobot

HomeRobot includes an ASP.NET Core backend for an ESP32 home robot and a native macOS robot simulator. It currently provides diagnostics and a versioned robot communication protocol. It supports text and Czech voice conversations through Ollama.

## Requirements

- .NET 10 SDK
- NuGet access to restore test dependencies

## Build and run

```sh
dotnet restore HomeRobot.slnx
dotnet build HomeRobot.slnx --no-restore
dotnet test HomeRobot.slnx --no-build
dotnet run --project src/HomeRobot.Server -- --urls http://localhost:5080
```

## Web dashboard

Run `dotnet run --project src/HomeRobot.Server` and open [http://localhost:5000](http://localhost:5000).
The command above uses [port 5080](http://localhost:5080) explicitly. The dashboard is served by the
.NET project as static HTML, CSS and JavaScript. It needs no separate frontend build or ESP32.

- **Overview, AI & Ollama, Voice, Vision** show live status and diagnostics. Ollama tests the
  effective URL and selected model. The Whisper check runs a silent PCM sample through its
  worker, and Piper synthesizes a Czech sample for playback. Camera, Auto Scan and change
  detection are managed in the macOS simulator.
- **Personality and Memory** use the existing profile and SQLite database. Bulk deletion
  requires typing `DELETE ALL`. No memory is stored in browser storage.
- **Robots and Logs** show P4 telemetry, command history and a bounded set of sanitized events.
  An Emergency Stop is confirmed only when the robot reports completion. The log buffer and
  runtime diagnostics reset on server restart.

Settings saves write a validated, non-secret JSON override file under the current account's
local application data directory at `HomeRobot/dashboard-settings.json`. Override its location
with `Dashboard__SettingsPath`. The file sits above the appsettings defaults and below user
secrets, environment variables and command-line configuration. Restart the server to apply
these overrides. The UI distinguishes saved overrides, effective values and deployment
overrides. Personality and memory edits take effect immediately. Voice models selected in the
dashboard must be under `models/stt/` or `models/tts/`. Executable paths and credentials are
configured by the server operator.

### Optional LAN access

By default the server listens on loopback and refuses nonlocal requests. For LAN access use
a direct Kestrel HTTPS listener bound to a private interface, with a certificate trusted by
the browsers and valid for the name or IP they use. Set `Dashboard__AllowLan=true` and set
`Dashboard__AdminPasswordHash` to the SHA-256 hex digest of a randomly generated password
of at least 24 bytes. For example, generate a password with `openssl rand -hex 24`, save it in
a password manager, and hash its exact text without a trailing newline. Keep the digest and
certificate credentials in your deployment secret configuration.

Set the listener URLs and certificate using the deployment configuration. For example, after
defining `LAN_IP`, `CERT_PATH` and injecting `Kestrel__Certificates__Default__Password`:

```sh
Dashboard__AllowLan=true Kestrel__Certificates__Default__Path="$CERT_PATH" \
  dotnet run --project src/HomeRobot.Server -- --urls "http://localhost:5000;https://${LAN_IP}:5443"
```

Open the HTTPS address and sign in as `admin` with the generated password. Nonlocal HTTP is
rejected. Remote REST and WebSocket clients also need HTTPS/WSS and HTTP Basic credentials.
Keep the macOS simulator on the server's loopback listener for its local voice and Vision
features. The loopback listener trusts processes on the server host. Restrict LAN access
with the host firewall, and avoid public exposure, reverse proxies and tunnels. HTTP Basic
credentials may remain cached in the browser until the browser session ends.

## Health check

```sh
curl http://localhost:5080/api/health
```

```json
{"status":"ok","service":"HomeRobot.Server"}
```

## Robot WebSocket

ESP32 devices and simulators connect to `ws://localhost:5080/ws/robot` and register with protocol version 1. The server tracks connection state, telemetry, and command execution. The P1 diagnostic `{"type":"ping"}` still returns `{"type":"pong"}`.

```sh
curl http://localhost:5080/api/robots
curl -X POST http://localhost:5080/api/robots/robot-001/stop
```

See the [robot protocol reference](docs/ROBOT_PROTOCOL.md) for message schemas, command submission, configuration, and device safety requirements.

## AI robot commands

With a robot connected, submit a Czech or English instruction. A valid plan returns HTTP 202 with its ID, interpreted actions, initial status, and a `Location` to poll.

```sh
curl -i http://localhost:5080/api/robots/robot-001/ai/command \
  -H 'Content-Type: application/json' \
  -d '{"message":"Otoč se doprava o 90 stupňů a popojeď 50 centimetrů."}'
curl http://localhost:5080/api/robots/robot-001/ai/plans/PLAN_ID
curl -X POST http://localhost:5080/api/robots/robot-001/stop
```

Plans support up to five actions using the P4 contracts. Write movement quantities as digits with units, such as `50 cm` or `90 degrees`. Distances support millimeters, centimeters, meters, and kilometers subject to the P4 limits. The server checks that the plan preserves explicit distances and angles in order. An omitted speed defaults to 30% or the configured maximum if lower. `set_expression` takes an expression identifier. `stop` can stand alone or end a plan. Use the direct P4 stop endpoint for emergencies.

The server validates every action against the configured P4 safety limits before dispatching any action. Ambiguous or invalid plans return HTTP 422 with `validationErrors`. A conflicting plan returns HTTP 409. Each accepted action waits for confirmed completion before the next starts. Failure, timeout, disconnect, or emergency stop ends the sequence and cancels remaining actions. The status endpoint includes per-action state and any P4 command ID. The latest 256 finished plans are kept in memory until restart or eviction.

The .NET tests use simulated AI replies and in-process WebSocket peers. The optional macOS integration test uses the running server and its configured Ollama endpoint.

## macOS simulator

Requires macOS 14 or later and Xcode 16 or later. Start the server on the simulator's default port, then open the project:

```sh
dotnet run --project src/HomeRobot.Server -- --urls http://localhost:5000
open src/HomeRobot.Simulator.Mac/HomeRobotSimulator.xcodeproj
```

Select the `HomeRobotSimulator` scheme and **My Mac**, then run with ⌘R. Click **Connect**. For the port 5080 setup above, enter `ws://localhost:5080/ws/robot`. The URL and simulator ID persist across launches. The robot endpoints trust loopback clients. Remote clients must satisfy the dashboard's LAN access policy.

In **AI Commands**, enter `Otoč se doprava o 90 stupňů a popojeď 50 centimetrů.` and click **Send**. Check that the plan lists the right turn first, then forward motion, and that each action completes in order. Watch the movement monitor and telemetry during execution. Submit an ambiguous instruction such as `Jeď někam dopředu.` to see the validation message. During a longer plan, click **Stop** to cancel remaining actions or **Disconnect** to halt the simulator and end the plan. The AI panel uses the REST API and displays the server's plan and action statuses. Its request timeout defaults to 150 seconds and can be set via the `aiRequestTimeoutSeconds` UserDefaults key if the server timeout is increased.

Set a distance, rotation angle, and speed, then click a direction. Watch the position, wheel speeds, and command status. Click **Stop** during a long move to verify that both wheels stop immediately. The buttons submit through P4 REST, and the simulator executes the commands it receives over WebSocket. To test a face expression, substitute the dashboard's robot ID:

```sh
curl -X POST http://localhost:5000/api/robots/ROBOT_ID/commands \
  -H 'Content-Type: application/json' \
  -d '{"command":"set_expression","parameters":{"expression":"happy"}}'
```

Run the Swift tests with ⌘U or:

```sh
xcodebuild -project src/HomeRobot.Simulator.Mac/HomeRobotSimulator.xcodeproj \
  -scheme HomeRobotSimulator -destination 'platform=macOS' test
```

With a server running, set `TEST_RUNNER_HOMEROBOT_INTEGRATION_WS=ws://localhost:5000/ws/robot` before the `xcodebuild` command to include the live P4 test. Add `TEST_RUNNER_HOMEROBOT_INTEGRATION_AI=1` to run seven Czech and English instructions through the configured Ollama endpoint and simulator. Close any running simulator first to avoid a duplicate robot ID.

The model assumes instantaneous speed changes, no wheel slip, an unbounded plane, and a fixed battery reading of 100%. Default simulated wheelbase is 31.83 cm and nominal wheel speed is 100 cm/s. Override `wheelbaseCm`, `maxWheelSpeedCmPerSecond`, or `telemetryHz` (8 by default, up to 30) in the app's UserDefaults domain `com.alkarpone.HomeRobot.Simulator.Mac` before launching. If server safety limits change, align `maxDistanceCm`, `maxRotationDegrees`, and `maxSpeedPercent` in the same domain. Slow commands can exceed the server's command deadline, which defaults to 10 seconds.

## Ollama setup and text chat

Run Ollama on the Windows PC with the `robot-ai` model alias already available and reachable from the Mac. Configure the server in `src/HomeRobot.Server/appsettings.json` or override the settings through environment variables. Replace `WINDOWS_PC_IP` with your PC's LAN address before starting the server on macOS.

```sh
Ollama__BaseUrl=http://WINDOWS_PC_IP:11434 dotnet run --project src/HomeRobot.Server -- --urls http://localhost:5080
```

Check `/api/ai/status` for `ollamaConnected: true`, `available: true`, and `model: "robot-ai"`.

| Setting | Environment variable | Default |
| --- | --- | --- |
| `Ollama:BaseUrl` | `Ollama__BaseUrl` | `http://localhost:11434` |
| `Ollama:Model` | `Ollama__Model` | `robot-ai` |
| `Ollama:TimeoutSeconds` | `Ollama__TimeoutSeconds` | `120` |

```sh
curl http://localhost:5080/api/ai/status
curl http://localhost:5080/api/ai/chat \
  -H 'Content-Type: application/json' \
  -d '{"message":"Ahoj, kdo jsi?","sessionId":null}'
```

The chat response contains `sessionId`, `response`, and `model`. Pass the returned session UUID in later requests to reuse context. Unknown or expired UUIDs start a new session. Messages can contain up to 8,000 characters. Sessions retain 20 conversation messages, expire after 30 minutes of inactivity, and are lost on restart.

Status returns `available`, `model`, and `ollamaConnected`. A missing model returns `available: false` and `ollamaConnected: true`. The server never downloads models. Chat returns HTTP 400 for invalid input, 502 for an invalid or failed upstream response, 503 for unavailable Ollama, a missing model, or full session capacity, and 504 for an Ollama chat timeout.

Automated tests use simulated Ollama responses.

## Local Czech voice

Use native Python (ARM64 on Apple Silicon, x64 on Windows) and run these commands from the repository root. Python 3.11 is recommended. macOS verification used Python 3.9 with faster-whisper 1.2.1 and Piper 1.8.0.

**macOS**

```sh
python3.11 -m venv .venv
.venv/bin/python -m pip install faster-whisper 'piper-tts>=1.3'
mkdir -p src/HomeRobot.Server/models/{stt/medium,tts}
.venv/bin/hf download Systran/faster-whisper-medium config.json model.bin tokenizer.json vocabulary.txt \
  --local-dir src/HomeRobot.Server/models/stt/medium
.venv/bin/python -m piper.download_voices --data-dir src/HomeRobot.Server/models/tts cs_CZ-jirka-medium
Ollama__BaseUrl=http://WINDOWS_PC_IP:11434 dotnet run --project src/HomeRobot.Server -- --urls http://localhost:5080
```

Whisper Medium needs about 1.5 GB of disk space. The Czech Piper model and configuration need about 63 MB. Replace `WINDOWS_PC_IP` with the Ollama host's LAN address. The server defaults to the `robot-ai` alias and Czech (`cs`) recognition. For the optional Python client, also install `sounddevice` and PortAudio.

**Windows PowerShell**

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install faster-whisper 'piper-tts>=1.3' sounddevice
```

Enable microphone access for desktop apps under **Settings → Privacy & security → Microphone**. Select input and output devices under **Settings → System → Sound**. The Piper Python package provides the executable on both platforms. A compatible local executable can be selected through the same setting.

**Model files**

Place existing models under `src/HomeRobot.Server/models`, or configure absolute paths. For the smaller Whisper option, explicitly download the [multilingual faster-whisper small snapshot](https://huggingface.co/Systran/faster-whisper-small/tree/main) to `models/stt/small` under the server directory. Place both [Czech Jirka medium voice files](https://huggingface.co/rhasspy/piper-voices/tree/main/cs/cs_CZ/jirka/medium), `.onnx` and `.onnx.json`, in `models/tts` under the same directory. Use the configured Ollama endpoint with the `robot-ai` alias already available. Server startup and requests use local files and do not download models.

| Setting | Environment override | Default |
| --- | --- | --- |
| Whisper Python | `Speech__STT__PythonPath` | Project `.venv` Python, then `python3` |
| Worker script | `Speech__STT__WorkerPath` | Bundled script |
| Whisper model directory | `Speech__STT__ModelPath` | `models/stt/small` if installed, otherwise `models/stt/medium` if installed |
| STT language | `Speech__STT__Language` | `cs` (empty enables detection) |
| Whisper vocabulary hint | `Speech__STT__InitialPrompt` | Short Czech movement and unit list in `appsettings.json` (empty disables it) |
| STT device and compute | `Speech__STT__Device`, `Speech__STT__ComputeType` | `cpu`, `int8` |
| Piper executable | `Speech__TTS__ExecutablePath` | Project `.venv` Piper, then `piper` |
| Piper voice | `Speech__TTS__VoiceModelPath` | `models/tts/cs_CZ-jirka-medium.onnx` |
| STT and TTS deadlines | `Speech__STT__TimeoutSeconds`, `Speech__TTS__TimeoutSeconds` | 60 seconds each |

Relative paths resolve from the server content root. Bare executable names use PATH. The TTS language comes from the selected voice.

**Push-to-talk client**

### Test voice in the macOS simulator

1. Open the simulator in Xcode and run it. Enter `ws://localhost:5080/ws/robot` and click **Connect**.
2. Click **Start recording** and allow microphone access. If you declined, enable HomeRobot Simulator in **System Settings → Privacy & Security → Microphone**. Select the microphone and speaker in **System Settings → Sound**.
3. Say “Kolik je dva plus dva?” and click **Finish recording**. Check the transcript, short Czech reply and playback. Use **Cancel playback** to stop speech.
4. Record “Otoč se doprava o devadesát stupňů rychlostí třicet procent.” Check that the plan shows `rotate_right` with 90°, the heading changes by 90°, and “Hotovo.” plays after confirmation.
5. Start a 100 cm forward move at 20% under **Manual Controls**. Click the red **Stop** while the robot is moving. Check that the speed drops to zero and the Stop command completes. Repeat the move and try “Robote, okamžitě zastav.” Also try the red **Stop** while recording to confirm that it discards the recording.

Recording ends automatically at 29 seconds. For movement, speak the direction and a distance or angle with its unit. Speed defaults to 30 percent or the configured maximum if lower. To override it for a move or turn, include a speed in percent in that action. Digits and common Czech spoken numbers work. For example, “Jeď vpřed o 80 cm při rychlosti 35 procent” and “Mohl bys prosím zacouvat o půl metru na 20 %?” both go directly to the robot command engine without an Ollama request. Join up to five complete actions with “a pak”. Consecutive movement imperatives also work without a conjunction, as in “Jeď rovně 10 centimetrů, odboč doprava o 45 stupňů.” Repeat the full instruction after a clarification. A clear standalone stop phrase goes straight to P4. The red **Stop** remains available when speech is unclear.

Compare **Raw Whisper** with **Corrected** to diagnose recognition. **Intent**, **Correction**, **Validation**, the plan, and its rejection reason show how far the request progressed. STT measures server transcription, Analysis measures local recognition and routing, and LLM measures the chat request for conversation. Total runs from the end of recording to playback start. The Whisper hint is a decoding suggestion. Keep it compact, and clear it if it biases everyday speech.

The optional live XCTest cases read `chat.wav`, `action.wav`, and `stop.wav` (16-bit PCM) from `TEST_RUNNER_HOMEROBOT_INTEGRATION_VOICE_WAVS` and connect using `TEST_RUNNER_HOMEROBOT_INTEGRATION_WS`. Use the phrases above. This exercises WAV input and the simulator without microphone capture.

### Optional Python client

Run the server as shown above. In another terminal use `.venv/bin/python tools/voice_client.py` on macOS or `.\.venv\Scripts\python.exe tools/voice_client.py` on Windows. Press Enter to start recording and Enter again to send. The client shows the transcription, reply and timings, plays the WAV, and keeps the session ID for the next turn.

Use `--list-devices` to find device indices and `--input-device` / `--output-device` to select them. If the microphone rejects 16 kHz, try `--sample-rate 48000`. Use `--server` to change the server URL.

**Voice API**

| Endpoint | Input | Success response |
| --- | --- | --- |
| `POST /api/voice/transcribe` | Multipart field `audio` | JSON `text` (empty for silence) |
| `POST /api/voice/synthesize` | JSON `text` (up to 8,000 characters) | `audio/wav` |
| `POST /api/voice/chat` | Multipart `audio` and optional UUID `sessionId` | JSON `sessionId`, `transcription`, `response`, `audioBase64` and `timings` |

Audio input accepts RIFF WAV with 16-bit PCM, one or two channels, and 8–48 kHz. Use 16 kHz mono where available. Limits are 30 seconds and 6 MiB per WAV. Chat timings report STT, LLM, TTS, and total processing time in milliseconds. Text and voice chat share sessions. Chat returns HTTP 422 if speech is absent.

```sh
curl -F 'audio=@sample.wav' http://localhost:5080/api/voice/chat
```

**Troubleshooting**

- **503** — Check the configured executable paths and the Python environment. Confirm `import faster_whisper` works, Whisper's directory contains the full snapshot, and Piper's model and JSON have matching filenames. Run `piper --help` in the selected environment and use a build that supports `--input-file` and `--output-file`. For Ollama, check `/api/ai/status`.
- **502** — A provider failed to return a result. Test the local runtime and model independently.
- **504** — Check the stage deadline. The first STT request includes model loading, and Piper loads its voice for each synthesis.
- **400 / 413** — Check PCM format, duration, and upload size. For microphone or playback errors, check device selection and permissions.

## Development status

See [project status](PROJECT_STATUS.md) for verification and [architecture](ARCHITECTURE.md) for the local pipeline.


## Personality and memory

Set `Personality` in server appsettings or use environment variables such as `Personality__Name`. The default profile uses brief, friendly Czech and light humor. The name is unset by default. Profile fields are `Name`, `Identity`, `Description`, `Language`, `CommunicationStyle`, `HumorLevel`, `Verbosity`, `DefaultExpression` and `SystemInstructions`. A profile saved through the API overrides appsettings across restarts. The simulator and WAV chat request brief Czech speech. Expression hints accept `idle`, `happy`, `sad`, `thinking` and `error`.

The SQLite file defaults to `HomeRobot/memory.sqlite3` under the server account's local application data directory. Set `Memory__DatabasePath` to an absolute path in a persistent, user-private directory to override it. The server initializes the schema. `Conversation__MaxHistoryMessages` sets the in-process history limit to an even number from 0 to 40 (default 20).

Use the same conversation session for follow-ups and confirmation. Supported Czech examples include:

- **Remember** — “Pamatuj si, že mám psa jménem Gessa.”
- **Recall** — “Jak se jmenuje můj pes?” or “Co si o mně pamatuješ?”
- **Update** — “Změň si, že můj pes se jmenuje Gessa, ne Gessi.”
- **Forget** — “Zapomeň, co jsem ti říkal o mém psovi.”
- **Bulk forget** — “Smaž všechny moje uložené preference” or “Smaž celou paměť”, followed by “Potvrzuji smazání” within two minutes in the same session.

Access management and conversational memory directly via `http://localhost:5080` (or another configured loopback port). The dashboard also supports management over authenticated HTTPS when LAN access is explicitly enabled. Conversational memory keeps its direct loopback boundary. Forwarded requests are blocked. The simulator uses the same local server via WebSocket and HTTP.

| Endpoint | Behavior |
| --- | --- |
| `GET /api/memory?category=preferences` | List records. Omit the query to list all. |
| `POST /api/memory` | Create from JSON `{"content":"Mám rád čaj.","category":"preferences"}`. |
| `PUT /api/memory/{id}` | Replace content and category using the same JSON shape. |
| `DELETE /api/memory/{id}` | Delete one record by ID. |
| `DELETE /api/memory` | Delete all with header `X-Confirm-Memory-Deletion: delete-all`. |
| `GET /api/personality` | Read the effective profile. |
| `PUT /api/personality` | Replace the profile with a JSON object using the profile fields above. |

Categories are `personal`, `preferences`, `people_pets`, `projects`, `routines` and `other` (API default). Only explicit commands and API writes persist facts. The secret filter rejects common credentials and key patterns. The database is plain SQLite. Store it on a private volume and use Ollama within the trusted local environment. Retrieved facts go to that configured model as untrusted data. Recent conversation remains in RAM, so earlier dialogue can still contain a fact after its persistent record is deleted. Czech intent and keyword matching are limited to supported phrasing and simple word variants. Use a more specific reference or the record ID when selection is ambiguous.

## Robot vision (macOS)

Run HomeRobot.Server on the same Mac as the simulator. Keep `Ollama:BaseUrl` pointed at the trusted LAN Ollama host and `Ollama:Model` set to the existing multimodal `robot-ai` alias (Qwen3-VL). The server uses [Ollama's image chat format](https://docs.ollama.com/capabilities/vision). The configured LAN connection carries the selected still to Ollama for inference.

In **Vision**, choose a camera if more than one is available, click **Start camera**, and allow the macOS prompt. To change permission later, open **System Settings → Privacy & Security → Camera**. Click **Capture image** to select a still, or **Import image…** to select a JPEG, PNG or supported HEIC/HEIF (up to 25 MiB). Enter an optional Czech question and click **Analyze**. Use **Stop camera** to end the preview, **Clear image** to release the selected still, or **Ask about previous image** to reuse the last successful analysis.

With the simulator connected, record “Co vidíš?” or “Rozhlédni se” in **Voice**. Whisper routes the request to Vision. The simulator starts the selected camera if needed, captures one still, and speaks the result. “Rozhlédni se” uses the current view. “Co jsi viděl?” uses the previous image, while “Co vidíš teď?” always captures a new one. A question such as “Jakou to má barvu?” reuses the previous image when available and takes a new still after expiry. The toolbar shows when the camera is active.

**Auto Scan** is off at launch. Enable it in **Vision** and select 10, 15, 30 or 60 seconds (default 30). Change detection is on by default with a 3.5% threshold. It compares a small grayscale sample against the last successful scan and can miss small or color-only changes or react to camera movement. Explicit requests always run inference. Scan results stay silent and do not dispatch robot actions. Disabling Auto Scan cancels the scan and leaves the preview available until **Stop Camera** or window closure. Diagnostics show timestamps, counts, change score, inference time and visual context age.

`POST /api/vision/analyze` accepts a Base64 JPEG/PNG up to 2 MiB and 4096 × 4096 pixels, an optional question of up to 500 characters, a UUID `sessionId`, and an optional ISO 8601 `capturedAt`. Omit `image` and use the returned `sessionId` to ask about the cached image. Set `scan: true` for stateless analysis. Configure the context lifetime with `Vision:ContextLifetimeSeconds` (default 120, range 1–600). `DELETE /api/vision/{sessionId}` clears the context. The API requires direct loopback access. Images stay in RAM, travel only to the configured Ollama endpoint for inference, and are never written to SQLite. Recognition, counts and OCR can be wrong, and voice routing covers common Czech phrases.
## Dual audio output (P13-A)

The macOS simulator's **Audio Output** panel independently enables Mac and ESP32
speakers, selects the physical robot and shows playback diagnostics. **Conversation
prompt → Send** and existing microphone turns use the same AI response and one Piper
synthesis. Mac playback/lip sync retain the original WAV; the ESP32 streams converted
16 kHz PCM through its existing SpeakerManager and I2S configuration. P10's **Robots**
view adds **Test Speaker**. See [ESP32 TTS setup, build/flash and hardware verification](docs/ESP32_TTS.md).
