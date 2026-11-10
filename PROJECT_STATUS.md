# Project status

## Repository checkpoint — 2026-11-11

- **Scope** — macOS simulator, validated robot commands, Czech voice routing, personality and memory, vision, web dashboard, and ESP32-S3 camera, OLED, speaker and microphone firmware.
- **Verification** — All 169 .NET tests passed. Dashboard JavaScript syntax validation passed. Two preserved serial-capture lines contain trailing whitespace. macOS and ESP32 builds were not rerun for this checkpoint.
- **Repository contents** — Include Xcode user state and pin the ESP-IDF checkout as a Git submodule alongside all project changes.

## ESP32-S3 I2S microphone input

- **Firmware** — I2S1 RX selects the left 24-bit Philips I2S sample from each 32-bit stereo frame and converts it to 16 kHz PCM16 mono. The microphone uses GPIO47/GPIO21/GPIO40 with the microSD slot empty. GPIO48 is wired to the onboard RGB LED. [Wiring and HTTP contract](HARDWARE.md#esp32-s3-i2s-mems-microphone)
- **Verification** — ESP-IDF 5.5.2 build and hash-verified application flash succeeded. The board returned 4,000-sample level JSON and an 80,000-sample WAV with varying nonzero PCM. An acoustic capture reproduced alternating 731 Hz and 1267 Hz tones played by the Mac. The user confirmed speech recording by playing back a fresh WAV. MJPEG delivered ten complete JPEG frames during the tested recording. Wi-Fi, still capture and speaker DMA playback responded. The OLED diagnostic ran and received no ACK. OLED pixels remain unconfirmed. The user confirmed audible MAX98357A test-tone playback on 2026-11-11.

## ESP32-S3 MAX98357A speaker output

- **Firmware** — I2S TX on GPIO41/GPIO42/GPIO14, with a quiet 440 Hz test tone and bounded mono PCM16 playback over HTTP. Each clip has its own software volume and playback is exclusive. See [HARDWARE.md](HARDWARE.md).
- **Server** — `Esp32SpeakerClient` accepts Piper WAV, converts the source sample rate to 16 kHz where needed and posts raw PCM to a selected device.
- **Verification** — ESP-IDF 5.5.2 build and verified USB application flash succeeded. Serial confirmed I2S initialization and completed tone, raw PCM and Czech Piper jobs. Live HTTP checks covered validation, upload timeout, busy rejection, camera capture and simultaneous MJPEG. All 35 focused .NET tests passed. The OLED scan still reported no ACK. The user measured 0.42 V on SD. An on-chip pulse-counter diagnostic verified both I2S clock rates and DIN activity at the ESP32 GPIOs. The user confirmed hearing the one-second 440 Hz test tone at 50% software volume on 2026-11-11.

## ESP32-S3 OLED bring-up

- **Face animation** — A dedicated task moves the eyes horizontally and varies the smile width and curvature. ESP-IDF 5.5.2 built the firmware, esptool verified the application-only write, and the OLED at `0x3C` acknowledged the startup and first updated frame. Camera capture and Wi-Fi startup succeeded. The visible motion awaits visual confirmation.

- **Startup face (2026-11-10)** — The user confirmed that the full-panel test lit the OLED. The firmware now writes a static monochrome smiley to SH1106 display RAM. ESP-IDF 5.5.2 built the firmware, esptool verified the application-only flash, and the OLED at `0x3C` acknowledged the frame. Camera capture and Wi-Fi startup succeeded. The smiley's appearance awaits visual confirmation.

- **Repeat scan (2026-11-10)** — The [USB serial capture](firmware/esp32-s3/oled-i2c-diagnostic/scan-serial-repeat.log) recorded 112 NACKs and zero ACKs or timeouts at each of 10, 50 and 100 kHz on GPIO1/GPIO2. The original camera application was restored with a verified flash write, and serial output confirmed camera capture and Wi-Fi startup.
- **Isolated scan (2026-11-09)** — The [standalone ESP-IDF diagnostic](firmware/esp32-s3/oled-i2c-diagnostic/README.md) built and ran on the ESP32-S3 with camera and Wi-Fi excluded. At each of 10, 50 and 100 kHz, all 112 addresses (0x08–0x77) returned NACK with zero ACK, timeout or other errors. The [USB serial capture](firmware/esp32-s3/oled-i2c-diagnostic/scan-serial.log) confirms GPIO1 and GPIO2 idle high with inputs enabled, peripheral-controlled open-drain outputs and internal pull-ups. The signal waveform and the OLED controller identity remain unverified.
- **Restoration** — The original application image matched the flash digest before the test. Esptool verified the application-only restore, and serial output confirmed a 640 × 480 OV5640 JPEG capture and a Wi-Fi DHCP address.
- **Firmware** — The one-shot scan of I2C0 runs at 100 kHz on SDA GPIO1 and SCK GPIO2. The SH1106 startup display accepts either `0x3C` or `0x3D`.
- **Hardware result** — The initial USB serial scan and three repeats on GPIO21/GPIO47 found no devices. After rewiring to GPIO1/GPIO2, esptool verified the application-only flash write and the serial scan again found no devices. OLED initialization and visible output remain unverified. The OV5640 and Wi-Fi started on the alternate-pin build.
- **Regression check** — The OV5640 captured a 640 × 480 JPEG at boot and Wi-Fi obtained a DHCP address. All four HTTP routes responded, including health and a 640 × 480 JPEG snapshot during a 640 × 480 MJPEG stream. See [PROGRESS.md](PROGRESS.md).
- **Repeat scans** — Two later GPIO1/GPIO2 scans timed out at their first probe (`0x08`). Camera and Wi-Fi started both times.
- **Diagnostic resets** — One GPIO1/GPIO2 scan completed with no responding devices and an unconfirmed wire state. The next scan again timed out at `0x08`. Camera and Wi-Fi started.
- **2026-11-10 hardware result** — I2C0 on GPIO1 (SDA) and GPIO2 (SCK) detected the OLED at `0x3C`. The module acknowledged the SH1106 initialization and all-pixels-on command. The OV5640 captured a VGA JPEG and Wi-Fi acquired a DHCP address.

## ESP32-S3 + OV5640 camera bring-up

- **Firmware** — The standalone `firmware/esp32-s3` ESP-IDF project serves health, device information, VGA JPEG snapshots and one concurrent MJPEG stream. The Wi-Fi provisioning tool reads a local ignored `.env` or JSON file and flashes a dedicated NVS partition.
- **Hardware** — ESP-IDF v5.5.2 built the firmware on Apple Silicon, and esptool verified the USB flash write. Boot confirmed ESP32-S3 v0.2, 16 MiB flash, 8 MiB octal PSRAM at 80 MHz, OV5640 ID `0x5640`, and a 640 × 480 JPEG. The supplied Dratek pinout confirms the camera GPIO mapping in the [firmware README](firmware/esp32-s3/README.md).
- **Wi-Fi verification** — After provisioning the updated SSID over USB, the ESP32 received `192.168.0.107` and the Mac reached all four HTTP routes. macOS decoded 640 × 480 JPEGs from both the snapshot and MJPEG stream. Health and snapshot requests succeeded during streaming, a second viewer received HTTP 503, and a new stream opened after the first disconnected. The board joined after two initial association retries.
- **Color shading investigation** — A temporary firmware build disabled the OV5640 lens correction and still showed a violet cast along the left edge. The reference captures varied in exposure and framing, so no spatial color correction was derived.
- **Camera orientation** — The firmware sets the OV5640 horizontal orientation at startup. Before-and-after captures showed the horizontal reversal. USB flash verification passed and all four HTTP routes worked over Wi-Fi, including snapshot capture during a VGA MJPEG stream. The color gradient remained visible.

## Czech voice command recognition

- **Completed** — Voice commands accept more Czech movement and rotation phrasing, polite requests, and additional percentage speed formats. The simulator submits recognized actions directly to P4 through the plan executor. The full plan still requires a direction and distance or angle with units for each movement, supported capabilities, and valid command limits.
- **Default speed** — Each voice move or turn without an explicit speed uses 30 percent or the configured maximum if lower. All 130 focused .NET tests passed after this change, including WebSocket dispatch at the default and explicitly requested speeds.
- **Consecutive movements** — The recognizer accepts successive movement imperatives with or without punctuation when the transcript omits a conjunction. All 137 focused .NET tests passed. The reported transcript ran in order over a simulated WebSocket, and parser tests checked that incomplete or negated instructions produce no actions.
- **Verification** — All 128 focused .NET tests passed and Xcode built the macOS simulator. A WebSocket test confirmed that the reported transcript dispatched a 100 cm forward move at 30 percent without an Ollama request. Tests also covered alternative phrasing, transcript binding, emergency stop, and rejection of a complete sequence with an out-of-range action. Physical microphone input remains to be checked.

## P10 — Web Dashboard and System Settings

- **Completed** — The responsive dark dashboard is served by HomeRobot.Server and covers Overview, AI & Ollama, Voice, Vision, Personality, Memory, Robots, Logs and Settings. It reuses the existing SQLite, speech and P4 paths. Health and robot views poll only while active, and Emergency Stop waits for the robot's terminal result.
- **Configuration and access** — Validated JSON overrides persist separately from appsettings and apply on restart. Deployment overrides retain precedence. Local access is enabled by default, with opt-in HTTPS and single-admin authentication for LAN clients. Diagnostic logs are bounded and use fixed sanitized messages.
- **Verification** — The server built with zero warnings and errors, JavaScript syntax validation passed, and all 152 selected .NET tests passed. A local browser showed live Ollama status and the connected macOS simulator, then displayed the simulator's completed Emergency Stop. An isolated server run verified persistence across restart, personality editing, memory CRUD, blocked cross-origin and nonlocal requests, a live Whisper probe and a Czech Piper WAV.
- **Limitations and next phase** — Ollama tests effective settings. Camera and Auto Scan controls remain in the simulator. Safari, Edge and a physical LAN HTTPS setup still need interactive verification. Verify these deployment paths and exercise voice and Vision with the physical microphone and camera before starting ESP32 integration.

## P9.2 — Autonomous Vision, Voice Integration and Visual Memory

- **Voice** — Normalized Czech vision requests start the selected camera on demand and capture one still through the shared session. Historical questions use only the previous still. Descriptive follow-ups reuse it until expiry and then capture a new still. Replies use the existing Piper and face pipeline. P4 emergency stop and movement validation stay on their existing paths.
- **Visual memory** — The loopback API keeps up to eight session-scoped images in RAM, each with its capture time and initial answer. The default lifetime is 120 seconds from capture. Follow-ups preserve expiry and explicitly identify cached observations. The visual prompt reuses the P8 personality and can reference the latest local conversation pair. No image enters SQLite.
- **Auto Scan** — Disabled on launch. The panel controls the interval, change detection and threshold, and reports camera activity, timings, results, skipped scans and cache age. Silent scan requests leave the conversation context untouched. The local grayscale heuristic compares against the last successful scan and can miss small or color-only changes. Stopping the camera or closing the dashboard also stops scanning.
- **Verification** — The .NET solution built and all 73 focused tests passed. The macOS simulator built, and 22 of 24 selected tests passed. All 13 Vision tests passed, including simulated camera failures, scan cadence, unchanged-frame skipping and cancellation. The live Czech WAV and synthetic-camera round trip verified Whisper, `robot-ai`, Piper, cached follow-up and fresh capture. The live P4 and voice emergency-stop tests passed. One live voice rotation test was rejected by the P7.2 transcription-correction validator, and the optional Piper WAV fixture test was skipped. Physical microphone and camera checks remain open.
- **Next phase** — Verify push-to-talk and Auto Scan on the physical camera and microphone, then define capability discovery and authenticated still-image delivery for ESP32.

## P9.1 — Live Webcam Preview and Simulator Vision

- **Local voice setup** — The server discovers Python and Piper in the repository virtual environment and falls back to Whisper Medium when the default Small model is absent. Explicit configuration takes precedence. All 32 focused voice tests passed, and a local run without speech overrides transcribed a Czech WAV and synthesized a Piper response.
- **Camera workflow** — In Vision, select a camera, click **Start Camera**, and grant camera access if macOS prompts. After denial, enable the simulator under **System Settings → Privacy & Security → Camera**. **Capture Image** takes a still without interrupting the live preview. Stop the camera before changing devices. A disconnected camera stops the session and the picker refreshes.
- **Image and voice** — The captured original stays in memory for preview until replaced, cleared, or the panel closes. **Analyze with AI** sends an oriented JPEG (up to 1280 pixels on the longest side and 2 MiB) and the editable Czech question, defaulting to “Co vidíš?”, through the existing local Vision endpoint to the configured Ollama `robot-ai`. Cancel interrupts the current request. **Speak Answer** uses the existing Piper player and face animation; **Stop Speech** interrupts playback. Voice-initiated turns still speak through the existing voice workflow.
- **Verification** — Xcode built the macOS app. Fourteen focused Vision and face tests passed; the optional Piper WAV fixture test was skipped. On a MacBook Pro with FaceTime HD, the simulator displayed live frames, captured and displayed a still with the preview running, then stopped while keeping the still. In a separate check using a synthetic PNG, `robot-ai` returned a Czech answer (about 6.6 seconds of inference), Piper returned WAV, and the simulator entered and left the speaking state. Audible output and visual mouth movement were not checked manually; automated tests covered playback and mouth-envelope behavior.
- **Limitations** — Vision requires HomeRobot.Server on the same Mac and a configured image-capable `robot-ai`; speech requires Piper. External camera switching and permission denial were not checked on physical hardware.

## P9 — Robot Vision and Camera Integration

- **Completed** — The macOS Vision panel provides user-started AVFoundation preview, device selection, still capture, native JPEG/PNG/HEIC import, cancellation and image results. Voice routing reuses Whisper normalization, Piper and the existing face states. Follow-ups reuse the previous still for three minutes and identify it as cached.
- **Server** — A loopback JSON endpoint validates and normalizes stills, then sends them to the existing Ollama `robot-ai` provider with the saved personality. Vision keeps at most eight image contexts in RAM and cannot dispatch commands or write long-term memory. P4 is unchanged.
- **Verification** — The .NET solution built and all 141 tests passed. Xcode built the simulator and passed 30 tests, including a live Czech WAV-to-Whisper-to-Qwen3-VL-to-Piper round trip with an imported red circle, a cached color follow-up, ordinary voice conversation, simulated movement and emergency stop. The first and cached vision HTTP requests took about 7.85 s and 1.84 s. Native PNG selection, preview and clear were checked in the app. Camera denial and no-device cases used automated tests; live camera preview, still capture and microphone input remain to be verified interactively.
- **Next phase** — Verify the camera and microphone on real hardware, then define capability advertisement and an authenticated still-image transport for ESP32.

## P8-C — Personality and Memory Management GUI

- **Completed** — The macOS dashboard opens a separate Robot Settings window with Personality, Memory and Diagnostics tabs. Memory supports listing, text search, server category filtering, refresh, create/edit and confirmed individual or bulk deletion. The profile editor supports all P8 fields, four draft presets, explicit save and discard.
- **Preview** — The loopback preview endpoint uses the unsaved draft and existing AI provider. It returns text without accessing memory or conversation history, playing audio, or dispatching movement.
- **Verification** — Xcode built the macOS app; 23 tests passed and 4 opt-in AI/voice tests were skipped. The passing tests include a live P4 round trip and Czech Piper sample playback. All 7 focused .NET memory tests passed. REST checks on a temporary database covered memory CRUD, bulk confirmation, profile persistence and preview validation. In the app, Robot Settings opened, the profile loaded, a memory was created, cancelling bulk deletion kept it in the list, and diagnostics reported the loaded record.
- **Limitations** — Management requires a server running on this Mac and uses the existing dashboard address. Diagnostics report loaded records, not database size or health. Preview needs the configured AI provider; automated preview generation used a mock. The live voice conversation still follows its existing Czech prompt. Real microphone and speaker checks remain open.
- **Next phase** — Verify live personality preview and Czech voice behavior with the configured AI, microphone and speakers.

## P8-A/B — Robot Personality and Long-Term Memory

- **Completed** — Configurable Czech personality, bounded per-session history, explicit memory commands and local management APIs. SQLite persists memory and API profile overrides. Retrieval uses category filters and bounded Czech keyword scoring.
- **Safety** — Memory operations run independently of Ollama. Bulk deletion requires confirmation against an unchanged selection. Conversation suggestions are allowlisted face expressions. P6 and P4 retain command interpretation, validation and dispatch.
- **Builds and tests** — The .NET solution built and all 115 tests passed. Xcode built the simulator and passed 16 tests, including the live P4 round trip and playback of a Czech Piper sample. Four opt-in AI and voice fixture tests were skipped in that Xcode run.
- **Live verification** — Whisper and Piper completed a Czech WAV remember, recall and forget sequence across a server process restart. The run verified Whisper's “drum & bass” variant and retained an unrelated pet record. Two successful `robot-ai` checks used the configured test personality and retrieved Gessa's name.
- **Limitations** — Keyword matching and Czech command phrases are limited. The loopback APIs use the server account's unencrypted SQLite file. Manual microphone and speaker checks remain open.
- **Next phase** — P8-C Memory Management GUI.

## P7.2 — Context-Aware Czech Speech and Robot Vocabulary

- **Completed** — Central Czech recognition vocabulary, common Whisper error normalization, and a configurable Czech initial prompt. Voice diagnostics show raw and corrected text, intent, correction and validation status, rejection reason, plan, and stage timings.
- **Safety** — Complete voice plans use one `robot-ai` request for correction and interpretation. They require explicit direction, distance or angle with units, and speed. The server checks every action against the transcript, P4 limits, and device capabilities before dispatch. Standalone clear stop goes directly to P4. Ambiguous, negated, unsupported, and incomplete instructions cannot authorize movement.
- **Verification** — The .NET server and test project built and 102 focused tests passed. Xcode built the macOS app and its test target. Three live WAV-based simulator tests passed with local Whisper Medium, Piper, and remote `robot-ai`: Czech conversation, a corrected 90° turn with manual stop, and voice emergency stop without an AI call. The final live run used two Ollama requests. Transcription took about 3.8–6.4 seconds per fixture and the combined command endpoint took about 1.13 seconds. A local routing sample took 1 ms.
- **Limitations** — Corrections are limited to known spelling and recognition variants. Unsupported hardware vocabulary is recognition-only. Clarification requires repeating the complete command. The live tests used WAV fixtures. Real microphone capture, speaker output, and reliability across speakers and room noise remain to be checked manually.

## P7.1 — Expressive Face and Audio-Driven Lip Sync

- **Completed** — The SwiftUI face has animated brows, subtle gaze shifts, irregular blinking, and smooth transitions across eight expressions. The mouth blends from closed to wide open during speech and curves into a smile for happy. Reduced Motion disables gaze and blink animations while preserving speech feedback.
- **Lip sync** — The existing AVAudioPlayer drives a precomputed 20 ms PCM RMS envelope with a configurable noise gate, fast attack, and slower release. The UI samples playback time and closes the mouth on completion, cancellation, or stopped output. Priority is voice state, active movement, then the saved P4 expression.
- **Verification** — Xcode built the macOS simulator. Fifteen simulator tests passed, including seven face and audio tests and playback of a Czech Piper Jirka Medium sample with a sentence pause. Three additional live tests passed against the local server with Whisper, Piper, and the configured Ollama model: P4 round trip, Czech conversation, and Czech movement with emergency stop. The idle face was inspected in the app window.
- **Manual verification pending** — Check mouth motion against audible syllables in the app window, the full expression transitions and blinking, and recovery from an audio-device interruption. The automated playback check uses the player clock and mouth openness values. The envelope follows amplitude and cannot represent individual speech sounds.

## P7 — Voice-Controlled Robot

- **Completed** — The macOS simulator records Czech push-to-talk WAV audio, shows the transcript, response, plan and stage timings, and plays Piper audio with cancellation. Voice states drive the face. The red Stop calls P4 directly, interrupts local voice activity, and invalidates pending voice instructions and plans.
- **Speech verification** — Apple Silicon Python 3.9 project virtual environment with faster-whisper 1.2.1 and Piper 1.8.0. Installed multilingual Whisper Medium (CTranslate2, CPU INT8 inference) and Czech Piper Jirka Medium. Local API tests returned a Czech transcript and mono 16-bit PCM Piper audio at 22.05 kHz.
- **Builds and tests** — The affected .NET projects built and all 75 focused tests passed. The macOS app built and all 10 tests in the final run passed. A live Czech conversation with the remote `robot-ai` passed its conversation checks in the first run. That run exposed Whisper's split spelling “do prava”. The corrected voice grammar passed the live 90° right-turn test and direct emergency stop in the final run. The live tests used two short Czech WAV fixtures and the actual simulator engine.
- **Limitations** — Real microphone capture, permission interaction and audible speaker output still need the manual README check. Voice motion accepts one explicit Czech movement with a numeric quantity and unit. Speech confidence is heuristic. No ESP32 is required.
- **Suggested next phase** — Validate live microphone behavior with several speakers and background noise, including rejected commands and Stop during each voice state.

## P6-B — Remote Ollama and simulator AI control

- **Completed** — The existing Ollama provider uses the `robot-ai` alias and a configurable base URL. The macOS simulator submits instructions through P6-A REST, polls plan and action states, and shows validation errors. Emergency stop uses P4 directly.
- **Manual controls follow-up** — The controls and telemetry views observe the connection service directly so they refresh when connection and command state change. The macOS app build succeeded.
- **Expression follow-up** — The AI prompt lists supported expressions and maps smile requests to `happy`. The interpreter also normalizes the model alias `smile` to `happy`. Both focused dispatch cases passed with a simulated provider.
- **Live verification** — The macOS server reached Ollama on the Windows PC and reported `robot-ai` available. Five valid prompts completed with the expected actions, order, and movement values. Both invalid prompts returned HTTP 422. The first run exposed an invented distance for an ambiguous prompt. The interpreter now verifies explicit numeric movement values against the source instruction before any action can dispatch.
- **Builds and tests** — The .NET server and test projects built with zero warnings and 47 focused tests passed. The macOS app and tests built and all 10 Swift tests passed, including the seven live AI prompts and the live P4 round trip with telemetry, emergency stop, and disconnect.
- **Limitations** — Movement quantities require digits and units. App window interaction and visual appearance are covered by the manual procedure in the README. The trusted-network and in-memory plan limitations remain.
- **Next task** — P7, Voice-Controlled Robot.

## P6-A — AI Command Engine

- **Completed** — Czech and English command interpretation through the P2 provider, strict JSON and P4 validation, and hosted sequential execution with per-action status. P4 emergency stop cancels plans, including during interpretation.
- **Verification** — The .NET 10 solution built with zero warnings and all 62 tests passed, including 16 cases with a mocked AI provider and in-process WebSocket peers. On macOS ARM64, eight Swift tests passed and the optional live P4 test was skipped.
- **Limitations** — Plans are in memory and robot endpoints are unauthenticated. Automated coverage verifies server behavior with simulated AI output.

## P5 — macOS simulator

- **Phase** — P5, native macOS SwiftUI robot simulator implemented for macOS 14+.
- **Completed** — Animated expressions, Canvas movement monitor, deterministic differential-drive motion, manual REST controls, P4 WebSocket registration and command execution, telemetry, connection diagnostics, and local stop on disconnect. The simulator uses protocol v1 without server changes.
- **Verification** — Xcode 16.4 built the app and test target on macOS ARM64. All nine Swift tests passed, including a live P4 round trip covering registration, manual REST submission, command acknowledgements and results, telemetry, expression changes, and emergency stop with a cancelled movement. All 46 existing .NET tests passed.
- **Limitations** — The simulator uses an unbounded plane, immediate speed changes, and a fixed battery reading. Reconnect is user-initiated. The server's command timeout applies to simulated moves. Robot endpoints remain unauthenticated and intended for trusted development networks.
