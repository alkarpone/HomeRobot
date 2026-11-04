# Project status

- **Phase** — P3, local Czech voice pipeline implemented.
- **Completed** — WAV transcription, speech synthesis, voice chat with shared P2 sessions and stage timings, and a cross-platform push-to-talk test client. P1 health and robot WebSocket and P2 text chat remain available.
- **Verification** — Solution build succeeded on macOS ARM64 with .NET SDK 10.0.403 and zero warnings. All 12 P3 tests with mock speech and AI providers and all 17 P1/P2 tests passed. The bundled Python worker returned its expected missing-model error in a subprocess smoke check.
- **Live audio** — Unverified on macOS and Windows. The available Python environment lacks faster-whisper, Piper, and sounddevice. Default local model directories are unprovisioned, and no live Ollama exchange was verified.
- **Limitations** — Input is limited to 30-second PCM WAV files. The Whisper worker handles one request at a time, Piper loads the voice for each synthesis, and session history is in memory.
- **Git baseline** — The P3 commit builds on the GitHub project scaffold and includes the P1 and P2 implementation.
- **Next task** — P4, ESP32 audio communication.
