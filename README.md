# HomeRobot

HomeRobot includes an ASP.NET Core backend for an ESP32 home robot and a native macOS robot simulator. It currently provides diagnostics and a versioned robot communication protocol. It supports local text and Czech voice conversations through Ollama.

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

Plans support up to five actions using the P4 contracts. Use explicit distances for `move_forward` and `move_backward`, and explicit angles for `rotate_left` and `rotate_right`. An omitted speed defaults to 30% or the configured maximum if lower. `set_expression` takes an expression identifier. `stop` can stand alone or end a plan. Use the direct P4 stop endpoint for emergencies.

The server validates every action against the configured P4 safety limits before dispatching any action. Ambiguous or invalid plans return HTTP 422 with `validationErrors`. A conflicting plan returns HTTP 409. Each accepted action waits for confirmed completion before the next starts. Failure, timeout, disconnect, or emergency stop ends the sequence and cancels remaining actions. The status endpoint includes per-action state and any P4 command ID. The latest 256 finished plans are kept in memory until restart or eviction.

The .NET tests mock the AI provider and use in-process WebSocket peers. P6-B will verify live Qwen output in both languages, rejection of ambiguous or unsafe requests, and end-to-end execution and emergency stop with the macOS simulator.

## macOS simulator

Requires macOS 14 or later and Xcode 16 or later. Start the server on the simulator's default port, then open the project:

```sh
dotnet run --project src/HomeRobot.Server -- --urls http://localhost:5000
open src/HomeRobot.Simulator.Mac/HomeRobotSimulator.xcodeproj
```

Select the `HomeRobotSimulator` scheme and **My Mac**, then run with ⌘R. Click **Connect**. For the port 5080 setup above, enter `ws://localhost:5080/ws/robot`. The URL and simulator ID persist across launches. The robot endpoints are unauthenticated and intended for trusted development networks.

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

With a server running, set `TEST_RUNNER_HOMEROBOT_INTEGRATION_WS=ws://localhost:5000/ws/robot` before the `xcodebuild` command to include the live P4 test. Close any running simulator first to avoid a duplicate robot ID.

The model assumes instantaneous speed changes, no wheel slip, an unbounded plane, and a fixed battery reading of 100%. Default simulated wheelbase is 31.83 cm and nominal wheel speed is 100 cm/s. Override `wheelbaseCm`, `maxWheelSpeedCmPerSecond`, or `telemetryHz` (8 by default, up to 30) in the app's UserDefaults domain `com.alkarpone.HomeRobot.Simulator.Mac` before launching. If server safety limits change, align `maxDistanceCm`, `maxRotationDegrees`, and `maxSpeedPercent` in the same domain. Slow commands can exceed the server's command deadline, which defaults to 10 seconds.

## Ollama text chat

Run Ollama locally with the configured model already installed. Configure the server in `src/HomeRobot.Server/appsettings.json` or override the settings through environment variables.

| Setting | Environment variable | Default |
| --- | --- | --- |
| `Ollama:BaseUrl` | `Ollama__BaseUrl` | `http://localhost:11434` |
| `Ollama:Model` | `Ollama__Model` | `qwen3:8b` |
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

Use native Python 3.11 (ARM64 on Apple Silicon, x64 on Windows) and run these commands from the repository root.

**macOS Apple Silicon**

```sh
python3.11 -m venv .venv
source .venv/bin/activate
python -m pip install faster-whisper 'piper-tts>=1.3' sounddevice
export Speech__STT__PythonPath="$PWD/.venv/bin/python"
export Speech__TTS__ExecutablePath="$PWD/.venv/bin/piper"
```

Allow your terminal or IDE under **System Settings → Privacy & Security → Microphone**. Select an input and output device in **System Settings → Sound**.

**Windows PowerShell**

```powershell
py -3.11 -m venv .venv
.\.venv\Scripts\python.exe -m pip install faster-whisper 'piper-tts>=1.3' sounddevice
$env:Speech__STT__PythonPath = Join-Path $PWD '.venv\Scripts\python.exe'
$env:Speech__TTS__ExecutablePath = Join-Path $PWD '.venv\Scripts\piper.exe'
```

Enable microphone access for desktop apps under **Settings → Privacy & security → Microphone**. Select input and output devices under **Settings → System → Sound**. The Piper Python package provides the executable on both platforms. A compatible local executable can be selected through the same setting.

**Model files**

Place existing models under `src/HomeRobot.Server/models`, or configure absolute paths. To provision models, explicitly download the [multilingual faster-whisper small snapshot](https://huggingface.co/Systran/faster-whisper-small/tree/main) to `models/stt/small` under the server directory. Place both [Czech Jirka medium voice files](https://huggingface.co/rhasspy/piper-voices/tree/main/cs/cs_CZ/jirka/medium), `.onnx` and `.onnx.json`, in `models/tts` under the same directory. Start Ollama with the Qwen model already installed. Server startup and requests use local files and do not download models.

| Setting | Environment override | Default |
| --- | --- | --- |
| Whisper Python | `Speech__STT__PythonPath` | `python3` |
| Worker script | `Speech__STT__WorkerPath` | Bundled script |
| Whisper model directory | `Speech__STT__ModelPath` | `models/stt/small` |
| STT language | `Speech__STT__Language` | `cs` |
| STT device and compute | `Speech__STT__Device`, `Speech__STT__ComputeType` | `cpu`, `int8` |
| Piper executable | `Speech__TTS__ExecutablePath` | `piper` |
| Piper voice | `Speech__TTS__VoiceModelPath` | `models/tts/cs_CZ-jirka-medium.onnx` |
| STT and TTS deadlines | `Speech__STT__TimeoutSeconds`, `Speech__TTS__TimeoutSeconds` | 60 seconds each |

Relative paths resolve from the server content root. Bare executable names use PATH. The TTS language comes from the selected voice.

**Push-to-talk client**

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
