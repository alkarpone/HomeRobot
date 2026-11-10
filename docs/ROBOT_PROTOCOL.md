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
