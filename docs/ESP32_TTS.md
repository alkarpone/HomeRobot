# P13-A: Piper speech on Mac and ESP32

## Reused pipeline

Text uses the simulator's **Conversation prompt → Send** and the existing `/api/ai/chat`
conversation. Microphone input retains Whisper, Czech correction, movement authorization,
memory and Vision routing. Both paths finish with one `/api/voice/synthesize` call.
Piper runs once. The original WAV drives Mac playback and the existing RMS lip sync.
When selected, the same WAV is converted by `Esp32SpeakerClient.ToPcm` for the ESP32.
The installed Czech Jirka Medium model declares 22,050 Hz; conversion reads the actual
WAV chunks rather than assuming that sample rate.

The physical device has its own stable `robotId` and uses the existing P4 registry,
heartbeat, command acknowledgements and history. Commands travel over WSS; PCM downloads
use HTTPS on the same configured origin. The server returns the WAV after queuing the
command, without waiting for a download or playback. ESP32 errors appear separately
from Mac playback. Speech is not synchronized between outputs.

## Server and device setup

1. Keep the simulator on the server's local listener, normally
   `ws://localhost:5080/ws/robot`. Enable the existing
   [LAN HTTPS and Basic authentication](../README.md#optional-lan-access).
   Use a certificate whose SAN matches the LAN hostname/IP. The device must be able
   to resolve that hostname. Do not use `localhost` as the device's server address.
2. Keep the server's existing Ollama `robot-ai`, Whisper and Czech Piper settings.
   Start the configured server with both local and LAN listeners, for example:

   ```sh
   Dashboard__AllowLan=true Kestrel__Certificates__Default__Path="$CERT_PATH" \
     dotnet run --project src/HomeRobot.Server -- \
     --urls "http://localhost:5080;https://${LAN_IP}:5443"
   ```

   Inject `Dashboard__AdminPasswordHash` and, when needed,
   `Kestrel__Certificates__Default__Password` through your private environment.
   `AdminPasswordHash` is SHA-256 of the exact admin password, without a newline.
3. In `firmware/esp32-s3`, create ignored `server.local.json`, mode `600`:

   ```json
   {
     "url": "https://YOUR_SERVER_HOST:5443",
     "robotId": "esp32-speaker-001",
     "password": "YOUR_EXISTING_ADMIN_PASSWORD_AT_LEAST_24_CHARACTERS",
     "caFile": "server.local.pem"
   }
   ```

   Put the server's issuing CA PEM certificate in `server.local.pem`. For a
   self-signed server certificate, use that PEM certificate. This is a public
   certificate, not a private key (at most 3,999 bytes for the NVS string). The URL must be an HTTPS origin without a
   trailing slash, path, query or embedded credentials. The firmware verifies the
   certificate and hostname. SNTP (`pool.ntp.org`, UDP 123) supplies time for TLS.
4. Build the existing project with the documented installed SDK:

   ```sh
   cd firmware/esp32-s3
   esp_setup="$HOME/.espressif/home-robot"
   export IDF_TOOLS_PATH="$esp_setup/tools"
   export PATH="$IDF_TOOLS_PATH/python/cpython-3.13.18-macos-aarch64-none/bin:$PATH"
   . "$esp_setup/esp-idf/export.sh"
   idf.py build
   ```

5. Close serial monitors, choose the current USB port, flash, and extend the existing
   provisioning using the same private Wi-Fi credentials already used by this board:

   ```sh
   port=/dev/cu.usbmodem5CE51304931
   idf.py -p "$port" app-flash
   python provision_wifi.py --port "$port" --server-config server.local.json
   idf.py -p "$port" monitor
   ```

   Substitute the port reported by `ls /dev/cu.usbmodem*` if needed. `app-flash`
   updates only the application and preserves partitions. Provisioning writes the
   existing `wifi_cfg` partition with the unchanged SSID/password plus server
   settings; it uses the existing ignored `.env`. If using `wifi.local.json`, add
   `--config wifi.local.json`. **Include `--server-config server.local.json` on future
   provisioning runs to retain server settings.** Provisioning resets the board.
   Exit the monitor with **Ctrl+]**. Never run `erase-flash` for this update.

No working GPIO mapping, Wi-Fi implementation, camera, microphone or OLED implementation
changes. SpeakerManager retains its I2S initialization, DMA buffers, mono-to-stereo
mapping and test tone. Its new stream reader supplies 160-frame PCM blocks at the
existing safe 20% volume. The HTTP client buffer is 2,048 bytes; no complete speech
clip is allocated on the ESP32. Legacy `/audio/play` retains its existing PSRAM upload
behavior and exclusivity. It is not the new TTS transport.

## Routing and controls

In **Audio Output**, **Mac Speakers** and **ESP32 Speaker** default to enabled and
persist through the existing UserDefaults settings mechanism. ESP32 delivery requires
an available device with all three capabilities: `audio_output`, `tts_playback`,
`audio_stop`. Choose **Physical robot** if multiple speakers are connected. The saved
selection survives disconnects; an empty selection automatically chooses the first
compatible device. Disconnection does not disable Mac playback.

**Stop Mac** affects local playback, including a response still being synthesized.
**Stop ESP32** targets the known pending/current remote playback ID. **Stop Playback**
cancels the current voice task and both outputs. Disabling an output also stops it.
The existing red **Stop** retains the movement emergency stop and stops audio.
A remote stop during synthesis records a short-lived cancellation so a late result
cannot start that playback. Stopping one destination does not stop the other.

The panel shows connection, audio capability, playback state and the latest error.
The P10 dashboard's **Robots** view shows the same remote state, result and error;
**Test Speaker** synthesizes one short Czech sentence with the existing Piper service.
Command acceptance is not playback completion. P4 `accepted` maps to `buffering`,
`executing` to `playing`, `completed` to `completed`, `cancelled` to `idle`, and
`failed` to `error`. A new connection starts with `idle`.

## Transport limits and recovery

- Raw signed PCM16 little-endian, 16,000 Hz, mono; at most 960,000 bytes / 30 seconds.
  The existing driver duplicates mono into both stereo I2S slots. Invalid or longer
  speech remains playable on Mac but reports an ESP32 format error.
- Up to 16 server resources / 15.36 MB of PCM in process memory. Resources expire
  after 45 seconds and are pruned every second or on access, including terminal
  command results and disconnection. No new audio files are written to disk.
- Downloads require existing server authorization **and** the unique command's
  256-bit token in `X-Audio-Token`. Tokens are not placed in URLs. Firmware accepts
  only `/api/audio/resources/{playbackId}` on its provisioned server origin and
  refuses redirects. No arbitrary playback URL is accepted through REST.
- Only one clip can own the speaker. Busy requests fail independently of Mac.
  Incoming control and outgoing status queues are bounded. The firmware remembers
  the last 256 command IDs and 64 playback IDs to reject repeats.
- Downloads have a two-second network-operation timeout and a forty-second overall
  deadline. The P4 audio command deadline is 45 seconds, separate from motion's
  unchanged deadline. Partial downloads stop I2S and report failure. The existing
  DMA underrun diagnostic remains active. Network stalls can cause audible gaps.
- A stop during synthesis is remembered for 90 seconds, with at most 256 pending
  cancellation records. Reconnection retries every three seconds, registers again,
  discards old connection traffic and cancels in-flight audio. Speech is never
  automatically replayed after reconnect.

## Physical verification checklist

These are manual acceptance tests. A successful firmware build or command acknowledgement
is not evidence that sound was audible. Do not repeat the already verified test tone.

1. After flashing/provisioning, observe `robot_audio: Server connected`. In the dashboard
   **Robots** view, verify `esp32-speaker-001` is separate from the simulator and reports
   TTS playback capability. If absent, check HTTPS binding, CA/SAN, admin credentials,
   DNS and SNTP; firmware without valid settings keeps its previous HTTP behavior.
2. Click **Test Speaker**. Serial should show command received, download started,
   buffering started, playback started and completed. Confirm the Czech sentence is
   audible through the physical MAX98357A speaker and that dashboard state reaches
   `completed`. `accepted` alone is insufficient.
3. Open/connect the Mac simulator. Enable both outputs and select the ESP32. Enter
   `Ahoj, jak se dnes máš?` under **Conversation prompt**, then **Send**. Confirm one
   displayed answer, the same spoken answer on Mac and ESP32, and preserved face/lip
   animation on Mac. Timing differences are expected.
4. Disable **Mac Speakers**, send another prompt, and confirm ESP32-only speech.
   Enable Mac, disable ESP32 and repeat for Mac-only speech.
5. Enable both, record a Czech microphone prompt, and finish recording. Verify Whisper
   transcription, the usual conversation/command route, and speech on both outputs.
   Existing movement commands must still require the simulator's advertised capability.
6. During a longer response, try **Stop Mac**, then on another response **Stop ESP32**.
   Confirm the other output continues. Try **Stop Playback** and disabling either output
   during synthesis. Confirm there is no late playback on a stopped output.
7. Disconnect/power off ESP32, submit a prompt and confirm Mac still speaks. Reconnect
   ESP32, wait for a fresh registration, and submit a new prompt. Restart the server
   and verify the device reconnects without replaying an old response.
8. Interrupt Wi-Fi during a longer clip. Confirm playback stops/errors, no clip stays
   permanently busy, and a later fresh request succeeds after reconnection. Check the
   existing camera snapshot/stream, microphone and OLED behavior after the update.

## Verification and limitations

Automated backend peers exercise registration, command/status transitions, token isolation,
expiry, cancellation, failure, disconnection and reconnection without hardware. Simulator
routing tests exercise Mac-only, ESP32-only, both and disabled outputs using injected
transports; existing face/player tests remain active. Builds do not validate acoustics,
real Wi-Fi/TLS timing, DMA behavior under camera load or microphone permissions.

The simulator retains its existing 500-character spoken-reply limit. Long clips are
not split automatically; ESP32 output is limited to 30 seconds.

The deployment reuses the current single-admin LAN authentication. There is no existing
per-device credential isolation; the ESP32 stores those credentials in the existing
plaintext development NVS partition. No new unauthenticated server route is added.
The board has not been flashed or provisioned as part of this implementation; configure
the real HTTPS endpoint before the manual tests. Hardware-dependent acceptance remains
pending. No new inference pipeline, audio decoder, Wi-Fi framework or I2S driver is added.

## Changed files

- Server: `src/HomeRobot.Server/Speech/AudioPlaybackService.cs`,
  `Speech/Esp32SpeakerClient.cs`, `Controllers/AudioController.cs`,
  `Controllers/VoiceController.cs`, `Communication/RobotProtocol.cs`,
  `Communication/RobotRegistry.cs`, `Program.cs`, `wwwroot/dashboard.js`.
- Firmware: `firmware/esp32-s3/main/robot_audio.cpp`, `main/robot_audio.h`,
  `main/main.c`, `main/CMakeLists.txt`, `main/idf_component.yml`,
  `components/speaker_manager/speaker_manager.cpp`,
  `components/speaker_manager/include/speaker_manager.h`, `dependencies.lock`,
  `provision_wifi.py`, `.gitignore`, `README.md`.
- Simulator: `src/HomeRobot.Simulator.Mac/HomeRobotSimulator/Services/VoiceInteraction.swift`,
  `Services/RobotConnectionService.swift`, `Views/SimulatorDashboardView.swift`,
  `HomeRobotSimulatorTests/VisionTests.swift`.
- Backend tests: `tests/HomeRobot.Tests/RobotAudioTests.cs`, `RobotProtocolTests.cs`,
  `DashboardTests.cs`.
- Project documentation: `ARCHITECTURE.md`, `PROJECT_STATUS.md`, `README.md`,
  `docs/ROBOT_PROTOCOL.md`, `docs/ESP32_TTS.md`.

Paths after the first full path in each group are relative to that project's root;
Swift service/view paths are relative to `HomeRobotSimulator/`. Existing unrelated
workspace changes are preserved.
