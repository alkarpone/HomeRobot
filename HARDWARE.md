# HomeRobot hardware

## ESP32-S3 N16R8 and MAX98357A

| MAX98357A pin | Connection |
| --- | --- |
| VIN | 5 V |
| GND | ESP32-S3 GND |
| BCLK | GPIO41 |
| LRC / LRCLK | GPIO42 |
| DIN | GPIO14 |
| GAIN | Open for the chip's default 9 dB gain |
| SD / SD_MODE | Open initially. See the module check below. |
| SPK+ and SPK− | Across the two speaker terminals |

Connect the speaker across the amplifier's differential outputs. Keep the ESP32
and amplifier grounds common. GPIO41, GPIO42 and GPIO14 are free in the
HomeRobot firmware. GPIO41 and GPIO42 also have external JTAG functions, while
the current console uses native USB Serial/JTAG and UART0. The camera uses the
[existing OV5640 pin map](firmware/esp32-s3/README.md#camera-wiring). OLED I2C0
uses SDA GPIO1 and SCL GPIO2.

The chip has an internal 100 kΩ pull-down on SD_MODE. A bare floating SD_MODE
pin selects shutdown. A breakout can enable the amplifier with an onboard
pull-up. For example, the Adafruit mono breakout uses 1 MΩ to VIN and selects
the stereo average at 5 V. Verify the installed module's schematic or measure
SD to GND with VIN powered and the SD header open. The datasheet gives typical
trip points of 0.16 V for enable, 0.77 V for right and 1.4 V for left. The
installed module measured 0.42 V with the SD header open.
[Analog Devices datasheet](https://www.analog.com/media/en/technical-documentation/data-sheets/max98357a-max98357b.pdf)
[Adafruit mono breakout pinout](https://learn.adafruit.com/adafruit-max98357-i2s-class-d-mono-amp/pinouts)

## Speaker firmware and HTTP

SpeakerManager initializes independently at boot. ESP-IDF 5.5.2 drives Philips
I2S with two 16-bit slots, a 16 kHz LRCLK and a 512 kHz BCLK. Each mono sample
is copied to both slots, so left, right and stereo average all carry the same
signal. The amplifier derives its timing from BCLK and LRCLK. The firmware
logs TX initialization, playback start and finish, failures, and DMA queue
overruns that indicate underruns.

| POST route | Request |
| --- | --- |
| `/audio/test` | Empty body. Plays a 440 Hz sine wave for 1 second at about 5% full-scale peak with the default volume. |
| `/audio/play` | Fixed-length `application/octet-stream` body with raw signed PCM16 little-endian samples, mono at 16 kHz. Send 2–960,000 bytes in complete samples, up to 30 seconds. |

Set the optional `X-Audio-Volume` header to an integer from 0 to 100. It scales
only the current clip and defaults to 20. The PCM upload is buffered in PSRAM
before playback, with one reservation across upload and playback. Supply the
sample rate and channel layout as specified above. Raw PCM bytes carry no
format metadata for the firmware to inspect.

Both routes return HTTP 202 when the job is accepted. HTTP 409 indicates
another job owns the speaker, 400 indicates invalid input, 408 an incomplete
or timed-out upload, 413 an oversized clip, 415 an unsupported media type,
and 503 an unavailable speaker or worker. Use the serial log to observe
completion. The camera and audio routes share the development LAN HTTP server.

```sh
base=http://DEVICE_IP
curl --fail -X POST "$base/audio/test"
curl --fail -H 'Content-Type: application/octet-stream' \
  -H 'X-Audio-Volume: 20' --data-binary @speech.pcm "$base/audio/play"
```

The test tone provides an audible check after wiring. Listen for one short
440 Hz tone, and record the result separately from the HTTP and serial checks.

## ESP32-S3 I2S MEMS microphone

| Microphone pin | ESP32-S3 connection |
| --- | --- |
| VDD | 3V3 |
| GND | GND |
| SCK / BCLK | GPIO47 |
| WS / LRCLK | GPIO21 |
| SD / DOUT | GPIO40 |
| L/R | GND (left slot) |

GPIO48 is routed to the onboard WS2812 RGB LED on the documented carrier.
Leave it disconnected from the microphone. GPIO47 and GPIO21 are available on
the HomeRobot header. GPIO40 is also wired to microSD D0. Keep the microSD
slot empty and its interface disabled while using this wiring. This mapping
avoids the camera, OLED, amplifier, native USB, UART0, flash, PSRAM and boot
strapping pins. [Carrier pinout](https://github.com/profharris/GOOUUU_ESP32-S3-CAM/blob/main/README.md#goouuu_esp32-s3-cam)

### Microphone firmware and HTTP

I2S1 RX is a Philips I2S master at 16 kHz WS and 1.024 MHz BCLK, with two
32-bit slots per frame. The driver reads both slots, and firmware keeps
the upper 16 bits of the signed, left-aligned 24-bit sample from the left slot.
Each request discards the first 500 ms after starting the clocks for microphone
startup.

| GET route | Response |
| --- | --- |
| `/mic/test` | Five seconds of WAV (`audio/wav`), exactly 80,000 signed PCM16 little-endian mono samples at 16 kHz. The `X-Mic-Signal` header reports signal quality. |
| `/mic/level` | JSON for a 250 ms capture window (4,000 samples), including `sampleRateHz`, `sampleCount`, `rms`, `peak`, `zeroSamples`, `railSamples` and `signal`. RMS and peak are in PCM16 counts (peak up to 32,768). |

The signal classification is `silent` for all zeros or RMS at most one PCM
count, `constant` for an unchanging nonzero value away from the rails, and
`invalid` when all samples are at the PCM limits. Other completed captures
report `ok`. These checks flag obvious faults and digital silence. An acoustic
test is needed to distinguish a working microphone from electrical noise.

Only one microphone request runs at a time. A competing request returns
HTTP 409. Initialization, allocation and I2S capture failures return HTTP 503.
The serial log records sample counts, RMS, peak, zero and rail counts, and
I2S errors. The HTTP server serves the camera and microphone concurrently on
the trusted development LAN. For future Whisper integration, send the WAV as
the multipart `audio` field to HomeRobot.Server at `POST /api/voice/transcribe`.

### Physical microphone check

With the board powered off, wire the microphone as above and leave the
microSD slot empty. After boot, use the DHCP address from the serial log.

```sh
base=http://DEVICE_IP
curl --fail "$base/mic/level"
curl --fail --dump-header mic.headers "$base/mic/test" --output mic.wav
```

Compare the level response in quiet conditions and while speaking near the
microphone. Speech should produce varying, nonzero PCM, with higher RMS and
peak than the quiet baseline. Speak during the five-second recording, check
for `X-Mic-Signal: ok` and listen for recognizable speech. Repeat the recording
with `/camera/stream` open and check both the playback and the live image.
Check the OLED pixel test and listen for the amplifier's `/audio/test` tone
to confirm the physical peripherals.
