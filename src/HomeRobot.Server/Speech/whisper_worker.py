"""Serve one base64 WAV per stdin line, emitting one JSON result per line."""
import base64
import io
import itertools
from pathlib import Path
import json
import sys


def main():
    model_path, language, device, compute_type = sys.argv[1:]
    first_request = sys.stdin.readline()
    if not first_request:
        return

    try:
        # A missing tokenizer would otherwise trigger faster-whisper's Hub fallback.
        if not (Path(model_path) / "tokenizer.json").is_file():
            raise FileNotFoundError("Local tokenizer is missing.")
        from faster_whisper import WhisperModel

        model = WhisperModel(
            model_path, device=device, compute_type=compute_type, local_files_only=True
        )
    except Exception:
        print(json.dumps({"error": "initialization"}))
        return

    for line in itertools.chain((first_request,), sys.stdin):
        try:
            audio = io.BytesIO(base64.b64decode(line.strip(), validate=True))
            segments, info = model.transcribe(
                audio, language=language, vad_filter=True, condition_on_previous_text=False
            )
            text = " ".join(s.text.strip() for s in segments).strip() if info.duration_after_vad > 0 else ""
            result = {"text": text}
        except Exception:
            result = {"error": "transcription"}
        print(json.dumps(result))


if __name__ == "__main__":
    main()
