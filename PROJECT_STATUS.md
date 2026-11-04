# Project status

## P6-A — AI Command Engine

- **Git scope** — The milestone commit combines the P4 robot protocol, P5 macOS simulator, and P6-A AI command engine.

- **Completed** — Czech and English command interpretation through the P2 provider, strict JSON and P4 validation, and hosted sequential execution with per-action status. P4 emergency stop cancels plans, including during interpretation.
- **Verification** — The .NET 10 solution built with zero warnings and all 62 tests passed, including 16 cases with a mocked AI provider and in-process WebSocket peers. On macOS ARM64, eight Swift tests passed and the optional live P4 test was skipped.
- **Limitations** — Plans are in memory and robot endpoints are unauthenticated. Automated coverage verifies server behavior with simulated AI output.
- **Next task** — P6-B. Verify local Qwen with Czech and English prompts, including valid multi-action plans and ambiguous or unsafe requests. Run end-to-end plans and emergency stop with the macOS simulator.

## P5 — macOS simulator

- **Phase** — P5, native macOS SwiftUI robot simulator implemented for macOS 14+.
- **Completed** — Animated expressions, Canvas movement monitor, deterministic differential-drive motion, manual REST controls, P4 WebSocket registration and command execution, telemetry, connection diagnostics, and local stop on disconnect. The simulator uses protocol v1 without server changes.
- **Verification** — Xcode 16.4 built the app and test target on macOS ARM64. All nine Swift tests passed, including a live P4 round trip covering registration, manual REST submission, command acknowledgements and results, telemetry, expression changes, and emergency stop with a cancelled movement. All 46 existing .NET tests passed.
- **Limitations** — The simulator uses an unbounded plane, immediate speed changes, and a fixed battery reading. Reconnect is user-initiated. The server's command timeout applies to simulated moves. Robot endpoints remain unauthenticated and intended for trusted development networks.
