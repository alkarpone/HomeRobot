"""Record a push-to-talk turn, send it to HomeRobot, and play the reply."""
import argparse
import base64
import io
import json
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen
import uuid
import wave

import sounddevice as sd


MAX_SECONDS = 30


def record(device, rate):
    audio = bytearray()
    max_bytes = rate * MAX_SECONDS * 2

    def capture(data, frames, time, status):
        remaining = max_bytes - len(audio)
        audio.extend(bytes(data)[:remaining])
        if len(audio) == max_bytes:
            raise sd.CallbackStop

    with sd.RawInputStream(
        samplerate=rate, channels=1, dtype="int16", device=device, callback=capture
    ):
        input(f"Recording for up to {MAX_SECONDS}s. Press Enter to send. ")
    with io.BytesIO() as buffer:
        with wave.open(buffer, "wb") as wav:
            wav.setparams((1, 2, rate, 0, "NONE", "not compressed"))
            wav.writeframes(audio)
        return buffer.getvalue()


def chat(server, wav, session):
    boundary = uuid.uuid4().hex
    body = [
        f'--{boundary}\r\nContent-Disposition: form-data; name="audio"; filename="voice.wav"\r\n'
        "Content-Type: audio/wav\r\n\r\n".encode("ascii"),
        wav,
        b"\r\n",
    ]
    if session:
        body.append(
            f'--{boundary}\r\nContent-Disposition: form-data; name="sessionId"\r\n\r\n{session}\r\n'
            .encode("ascii")
        )
    body.append(f"--{boundary}--\r\n".encode("ascii"))
    request = Request(
        server.rstrip("/") + "/api/voice/chat",
        data=b"".join(body),
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
    )
    with urlopen(request, timeout=300) as response:
        return json.load(response)


def play(data, device):
    with wave.open(io.BytesIO(base64.b64decode(data)), "rb") as wav:
        if wav.getsampwidth() != 2:
            raise ValueError("Expected 16-bit PCM from the server.")
        with sd.RawOutputStream(
            samplerate=wav.getframerate(), channels=wav.getnchannels(), dtype="int16", device=device
        ) as stream:
            while chunk := wav.readframes(4096):
                stream.write(chunk)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server", default="http://localhost:5080")
    parser.add_argument("--list-devices", action="store_true")
    parser.add_argument("--input-device", type=int)
    parser.add_argument("--output-device", type=int)
    parser.add_argument("--sample-rate", type=int, default=16000, choices=(16000, 44100, 48000))
    args = parser.parse_args()
    if args.list_devices:
        print(sd.query_devices())
        return

    session = None
    while input("Press Enter to record or type q to quit. ").strip().lower() != "q":
        try:
            result = chat(args.server, record(args.input_device, args.sample_rate), session)
            session = result["sessionId"]
            print("You:", result["transcription"])
            print("Robot:", result["response"])
            print("Timings:", result["timings"])
            play(result["audioBase64"], args.output_device)
        except HTTPError as error:
            with error:
                try:
                    message = json.load(error).get("title", error.reason)
                except ValueError:
                    message = error.reason
            print(f"HTTP {error.code}: {message}")
        except (URLError, TimeoutError, sd.PortAudioError, ValueError, wave.Error) as error:
            print(error)


if __name__ == "__main__":
    try:
        main()
    except (KeyboardInterrupt, EOFError):
        print()
