# Robot protocol v1

ESP32 devices and the P5 macOS simulator use `/ws/robot`. Each WebSocket message is one UTF-8 JSON object. Fragmentation is supported. Field names and identifiers are case-sensitive. A v1 envelope contains exactly `id`, `type`, `timestamp`, and an object `payload`. Use a new UUID for each message and an ISO 8601 UTC timestamp with `Z` or `+00:00`. Identifiers use 1–64 ASCII letters, digits, dots, underscores, or hyphens. The server generates outbound IDs and rejects duplicate device IDs among the last 256 message IDs on each connection. Client timestamps are informational.

## Register and stay connected

Open the socket, send `register`, and wait for `register_ack` before using the device protocol. Use a stable robot ID, a name of 1–100 characters, and up to 32 distinct capability identifiers. Capabilities describe the device for clients.

```json
{"id":"msg-001","type":"register","timestamp":"2026-11-04T18:00:00Z","payload":{"robotId":"robot-001","name":"HomeRobot Simulator","deviceType":"simulator","protocolVersion":1,"capabilities":["movement","display","audio"]}}
{"id":"srv-001","type":"register_ack","timestamp":"2026-11-04T18:00:00Z","payload":{"robotId":"robot-001","protocolVersion":1,"heartbeatIntervalSeconds":5,"inactivityTimeoutSeconds":15}}
{"id":"msg-002","type":"heartbeat","timestamp":"2026-11-04T18:00:05Z","payload":{"robotId":"robot-001"}}
```

Device types are `esp32` and `simulator`. The server rejects unsupported versions and simultaneous registrations of the same robot ID. Send a heartbeat at the advertised interval even while moving. Valid heartbeats and telemetry for the registered ID refresh last seen. The server measures deadlines using its monotonic clock and disconnects after the advertised inactivity timeout. The same deadline applies from connection until registration. Successful heartbeats, telemetry, acknowledgements, and results have no response. The P1 diagnostic `{"type":"ping"}` still returns `{"type":"pong"}` and does not refresh last seen.

## Commands

Submit `{"command":"move_forward","parameters":{"distanceCm":50,"speed":30}}` via REST. The device receives a `command` envelope with that payload and uses the envelope ID as its command ID.

| Command | Parameters |
| --- | --- |
| `move_forward`, `move_backward` | `distanceCm` in cm, greater than 0 through 100 by default. `speed` is greater than 0 through 50 by default. |
| `rotate_left`, `rotate_right` | `angleDegrees` is greater than 0 through 180 by default. Same `speed` range. |
| `set_expression` | `expression` is an identifier such as `happy`. |
| `stop` | Empty object. Uses the emergency stop path. |

Speed is a percentage of the device's nominal speed scale. Commands require JSON numbers and accept only the listed parameter fields.

```json
{"id":"cmd-001","type":"command","timestamp":"2026-11-04T18:00:10Z","payload":{"command":"move_forward","parameters":{"distanceCm":50,"speed":30}}}
{"id":"msg-003","type":"command_ack","timestamp":"2026-11-04T18:00:11Z","payload":{"commandId":"cmd-001","status":"accepted"}}
{"id":"msg-004","type":"command_ack","timestamp":"2026-11-04T18:00:12Z","payload":{"commandId":"cmd-001","status":"executing"}}
{"id":"msg-005","type":"command_result","timestamp":"2026-11-04T18:00:13Z","payload":{"commandId":"cmd-001","status":"completed","message":null}}
```

Commands start as `pending`. After dispatch, acknowledge `accepted`, then optionally `executing`. A `completed` result requires one of those two states. A dispatched command may report `failed` or `cancelled` from any nonterminal state, including rejection before acceptance. The optional result message allows up to 256 characters. For movement, report any terminal result only after motion has stopped. The server rejects out-of-order or repeated transitions, including results for a movement command already cancelled by a stop.

One movement may be active per robot. Emergency stop takes priority over queued messages after registration and cancels the tracked movement immediately. A frame already being sent may arrive before stop. The device must preempt motion upon receipt, even while processing other commands, and report stop completion after motion ceases. The server blocks new movement until stop completes and coalesces concurrent stop requests while a stop is active. A failed or cancelled stop disconnects the device.

The command deadline starts at REST submission. Expiration marks the command failed. A stop timeout or a dispatched movement timeout also disconnects the device. Connection loss cancels remaining active commands. The server never retries movement automatically.

## Telemetry

```json
{"id":"msg-006","type":"telemetry","timestamp":"2026-11-04T18:00:15Z","payload":{"robotId":"robot-001","state":"idle","position":{"x":0,"y":50,"heading":90},"movement":{"speed":0,"leftWheel":0,"rightWheel":0},"batteryPercent":100}}
```

Use a device-local Cartesian plane with origin at startup, +X right and +Y forward relative to the initial heading. Positions are in centimeters. Heading 0 faces +Y and increases clockwise toward +X in degrees in the range [0, 360). Movement speed is an unsigned percentage from 0 to 100. Wheel speeds range from −100 to 100 percent, with positive values turning forward. Battery is 0–100 percent. Use a state identifier such as `idle`, `moving`, or `error`. Values are device reports, and the server validates shape and range.

## REST and errors

| Request | Success |
| --- | --- |
| `GET /api/robots` | 200 with an array of connected robot details |
| `GET /api/robots/{robotId}` | 200 with registration fields, `status`, UTC `lastSeen`, and latest `telemetry` or null |
| `POST /api/robots/{robotId}/commands` | 202 with command status and a `Location` to poll |
| `POST /api/robots/{robotId}/stop` | 202 with command status and a `Location` to poll |
| `GET /api/robots/{robotId}/commands/{commandId}` | 200 with command ID, name, state in `status`, timestamps, and optional message |

REST returns 400 for invalid commands, 404 for disconnected robots on submission or missing resources on lookup, 409 for movement conflicts, and 429 for queue saturation. The latest 256 terminal commands remain queryable across disconnects. The registry and history reset on server restart.

Protocol errors use an envelope with `type: "error"` and payload such as `{"code":"duplicate_robot_id","replyTo":"msg-001"}`. `replyTo` is null when a valid incoming ID is unavailable. Legacy diagnostic errors and unparseable JSON may use `{"type":"error","code":"invalid_json"}` with a top-level code. Errors generally leave the connection usable. A message exceeding the configured byte limit closes the socket with code 1009. Binary frames are unsupported.

Configure `RobotProtocol` in `appsettings.json` or via environment variables such as `RobotProtocol__CommandTimeoutSeconds`. Defaults are 4096 bytes, a 5-second heartbeat interval, 15 seconds of inactivity, and a 10-second command timeout. Movement limits are `MaxDistanceCm`, `MaxSpeed`, and `MaxRotationDegrees`.

## Device safety and P5

ESP32 firmware must independently implement a motor watchdog, emergency stop, communication timeout, motor speed limits, and obstacle detection when sensors are available. A WebSocket disconnect alone cannot provide physical safety. Use this unauthenticated endpoint on a trusted development network.

P5 should use a stable robot ID, `simulator` registration, the advertised heartbeat schedule, and the same envelope and validation rules as ESP32. Keep the receive loop active during simulated motion so stop can preempt it. Send telemetry at startup and as simulated state changes. On loss of communication, halt simulated motion locally, reconnect and register again, and discard prior in-flight work.

## P13-A audio commands

Audio-capable ESP32 devices register separately from simulators with `audio_output`,
`tts_playback`, and `audio_stop`. No motor or microphone capability is implied.
The existing LAN HTTPS/Basic boundary also authenticates WSS and audio downloads.

The server prepares playback through `POST /api/voice/synthesize` with optional
`esp32RobotId` and `playbackId` identifiers. The response remains the original WAV.
`X-Playback-Id` identifies accepted remote delivery; `X-Audio-Error` reports an
independent routing/format/busy error without losing the Mac WAV. The client uses
one synthesis call per reply. Omitting the device preserves the existing endpoint.

A device receives the usual `command` envelope; its envelope ID remains the P4 command ID:

```json
{"id":"cmd-audio-1","type":"command","timestamp":"2026-11-11T10:00:00Z","payload":{"command":"audio_play","parameters":{"playbackId":"playback-1","resource":"/api/audio/resources/playback-1","token":"64_UPPERCASE_HEX_CHARACTERS","sampleRate":16000,"channels":1,"bitsPerSample":16,"byteLength":32000,"encoding":"pcm_s16le"}}}
```

`resource` is strictly relative to the provisioned server origin. Download it with
Basic authorization and `X-Audio-Token: <token>`; redirects are forbidden. The body
is raw PCM, not WAV. Require exact `Content-Length`, complete samples, PCM16LE mono
16 kHz and at most 960,000 bytes. Reject unsupported metadata before playback.
Tokens are scoped to one resource and expire after 45 seconds; terminal results,
disconnection and restart also invalidate resources. Audio bytes never travel in WSS.
Do not log tokens or put them into URL query strings.

Acknowledge `accepted` after validating/reserving the job (`buffering`), then
`executing` only when PCM playback actually starts (`playing`). Send `command_result`
with `completed` only after I2S finishes. Network interruption, incomplete downloads,
format mismatch or output failure produce `failed`; cancellation produces `cancelled`
after I2S stops. Existing P4 ordering and duplicate-result rejection apply. Audio's
45-second deadline does not change movement deadlines. Overlapping clips are rejected.

Targeted stop uses a new command ID and the original playback ID:

```json
{"id":"cmd-audio-stop","type":"command","timestamp":"2026-11-11T10:00:01Z","payload":{"command":"audio_stop","parameters":{"playbackId":"playback-1"}}}
```

Acknowledge the stop, cancel the matching play command, then complete the stop once
output has stopped. An unrelated/stale playback ID cannot stop another clip. The
firmware cancels playback locally on connection loss and never resumes old speech.

`GET /api/robots` and individual robot details add nullable `audio` with `playbackId`,
`status` (`buffering`, `playing`, `completed`, `idle`, `error`) and nullable `error`.
A null value means no audio request has been tracked on this connection. P4 command
history retains the last result across disconnects as before.

| Additional REST request | Behavior |
| --- | --- |
| `GET /api/audio/resources/{playbackId}` | Authenticated scoped PCM download; missing, invalid, expired or released token returns 404. |
| `POST /api/audio/{robotId}/stop` with `{"playbackId":"..."}` | 202 for a stop or pending synthesis cancellation; 404 disconnected, 400 unsupported, 429 bounded capacity. Cancelling a not-yet-dispatched ID is remembered for 90 seconds. |
| `POST /api/audio/{robotId}/test` | One Czech Piper synthesis and remote dispatch; 202 queued, 409 delivery rejected. |

Playback URLs and metadata cannot be supplied through the general REST command endpoint;
only the server prepares `audio_play`. `audio_stop` is also accepted through existing
P4 command submission. Detailed [configuration and physical tests](ESP32_TTS.md).

## P13-B face synchronization

Supported logical expressions: `idle` (neutral), `happy`, `sad`, `angry`, `surprised`,
`eyes_closed`, and existing `listening`, `thinking`, `speaking`, `error`. `blink` is a
180 ms temporary eye closure, never a replacement for the base expression. `executing`
remains a simulator fallback, not an executable expression. Unknown identifiers return
400; the existing `smile` alias normalizes to `happy`.

Common Czech commands reuse `VoiceIntent` and normalization: “Zamrač se”, “Usměj se”,
“Buď smutný”, “Překvap se” / “Vypadáš překvapeně”, “Zavři oči”, “Mrkni”,
“Vrať se do normálu” / “Otevři oči”. These standalone commands need no Ollama request.
Voice retains Whisper confidence checks and the existing one-use authorization.
Simulator text chat includes optional `robotId` in POST `/api/ai/chat`; recognized face
commands return `faceCommand: true` and update state even if the display is disconnected.
Without `robotId`, chat retains its prior behavior.

### Display notifications and state

Advertise `display` and `face_sync` on the existing `/ws/robot` registration. An optional
`faceRobotId` identifies the logical robot to mirror; otherwise it equals `robotId`.
Every physical connection still needs a distinct `robotId`. After `register_ack`, the
server sends the current state and subsequently sends only changed face state:

```json
{"id":"face-1","type":"face_state","timestamp":"2026-11-11T20:00:00Z","payload":{"expression":"angry","revision":1,"lastCommand":"angry","blinkAt":null,"displayClients":["sim-123"]}}
```

`blinkAt` marks a live blink event; receivers ignore events older than 500 ms and
restore the base expression after 180 ms. Re-registration sends `blinkAt: null`.
Repeated base expressions produce no notification; queued obsolete face notifications
are coalesced without broadcasting telemetry or other state. For `face_sync` clients,
P4 `set_expression` completes when the authoritative state is accepted and notifications
are queued, independently of physical delivery. It does not imply an OLED completion ACK.
Legacy clients keep command/ack/result behavior. State survives client disconnections,
but resets on server restart. The runtime holds at most 256 logical robot face states.

GET `/api/robots/{robotId}/face` returns the current base expression, revision, last
expression command, blink timestamp and connected WebSocket display client IDs.
GET `/api/face/esp32` returns the configured logical robot ID and HTTP delivery status
(`disabled`, `connecting`, `delivered`, `error`). The HTTP display is represented by this
separate diagnostic, not by a fabricated WebSocket registration.

### Existing firmware HTTP extension

The existing ESP-IDF source now adds GET and POST `/face` to the camera/audio HTTP
server. POST accepts exactly one allowlisted expression property:

```http
POST /face
Content-Type: application/json

{"expression":"angry"}
```

202 JSON `{"status":"accepted"}` means accepted by the OLED task; malformed/unknown
expressions return 400 and unavailable OLED returns 503. GET returns
`{"expression":"angry"}` for the base state (503 if unavailable). Bodies are limited
to 95 bytes and require a fixed `Content-Length`; chunked JSON uploads are not supported
by this existing ESP-IDF HTTP handler. The server client serializes the small payload
before sending it. The existing trusted-LAN HTTP access model is unchanged; do not expose
this device server to the Internet. No arbitrary drawing API is provided.

The existing SH1106 128×64 initialization, 132-column RAM offset, GPIO1/2 and address
detection at 0x3C/0x3D remain. Monochrome eyes, brows and mouths use integer primitives.
The OLED task polls state at 20 ms and writes display pages only when the visible pose
changes. POST never writes I2C or waits for playback. Wi-Fi, camera, microphone and
SpeakerManager/I2S paths are unchanged. HTTP delivery probes every five seconds, retries
only after a five-second backoff, and restores the latest expression after a reboot or
connection failure. It never replays a stale blink or speech clip.

### Configuration and manual hardware test

1. Build the existing firmware with the SDK command in [ESP32 TTS](ESP32_TTS.md).
   **Do not flash without explicit user approval.** The previously deployed firmware
   lacks `/face`; server/simulator testing can proceed before firmware deployment.
2. Connect the rebuilt simulator and obtain its stable `robotId` from GET `/api/robots`
   or the simulator connection diagnostics. Add these environment settings to the
   existing server launcher configuration, substituting the real ID/address, and restart:

   ```sh
   export Face__Esp32__Enabled=true
   export Face__Esp32__RobotId=sim-YOUR-STABLE-ID
   export Face__Esp32__BaseUrl=http://DEVICE_IP
   ```

   `BaseUrl` may be omitted to reuse `Speech:Esp32:BaseUrl`. Delivery defaults to disabled.
   No Wi-Fi provisioning or server listener change is required.
3. After the separately approved firmware deployment, GET `http://DEVICE_IP/face` must
   return 200. GET `/api/face/esp32` must report `delivered`.
4. Enter “Zamrač se.”, “Usměj se.”, “Buď smutný.”, “Vypadáš překvapeně.”,
   “Zavři oči.” and “Vrať se do normálu.” in the simulator text field. Verify matching
   logical expressions on Mac and OLED and inspect `/api/robots/{robotId}/face`.
5. Set happy, then enter “Mrkni.” Verify a brief eye closure on both and return to happy.
   Say “Buď smutný.” via the existing push-to-talk workflow to verify Whisper routing.
6. Disconnect ESP32, change the Mac expression, then reconnect ESP32. The Mac must stay
   responsive and OLED must restore the latest expression within roughly five seconds
   plus request time. Reconnect the simulator and verify its base state is restored.
7. Generate an ordinary Piper reply with both existing audio outputs enabled. Verify
   audible Mac/ESP32 output, Mac lip sync, and retained logical emotion on both displays.
   While playback runs, POST a valid `set_expression` through the existing robot command
   endpoint and check that speech continues. Check camera/microphone functions as usual.

Physical pixels, audible output and coexistence under hardware load require manual
verification. Automated tests use simulated WebSocket and HTTP peers, not an ESP32.

Deployment note (2026-11-11): the user approved application-only flashing and `/face`
was verified on the physical device. Private launcher face settings are configured.
Live text commands, synthetic Whisper voice routing, Piper HTTP audio acceptance,
simulator reconnect and remote-state reconciliation passed. Visual/acoustic checks
and real Wi-Fi/power recovery remain manual.
