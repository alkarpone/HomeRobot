# HomeRobot progress

## 2026-11-11 — Speaker listening confirmation

The device accepted `POST /audio/test` with `X-Audio-Volume: 50` (HTTP 202). The user confirmed hearing the one-second 440 Hz tone through the MAX98357A and connected speaker.

## 2026-11-10 — ESP32-S3 I2S microphone

- The carrier pinout routes GPIO48 to the onboard WS2812 LED. The microphone uses SCK GPIO47, WS GPIO21 and SD GPIO40 with the microSD slot empty and disabled.
- Implemented independent I2S1 RX, a 250 ms level endpoint and a five-second WAV endpoint. An initial board capture showed an alternating constant slot. The final build reads both 32-bit Philips slots explicitly and keeps only the left slot, producing signed PCM16 mono at 16 kHz. Capture diagnostics report levels, zero and rail counts, signal classification and driver errors.
- ESP-IDF 5.5.2 built the final firmware. Esptool flashed only the factory application at `0x10000` and verified the image hash. Serial confirmed I2S RX initialization. The test decoded the 160,044-byte WAV as 80,000 mono PCM16 frames at 16 kHz and checked for the alternating-slot regression. The clip had RMS 4,505.08, peak 15,829, ten zero samples and no rail samples. A competing level request returned HTTP 409.
- The final build captured alternating 731 Hz and 1267 Hz tones played acoustically by the Mac. The corresponding frequency components followed the playback sequence in the WAV. This confirms that the nonzero samples carry real sound.
- During the WAV capture, the test received ten complete MJPEG frames, a JPEG snapshot and HTTP 202 for speaker playback. Serial confirmed completion of the test tone. Wi-Fi joined using its existing provisioning. The OLED startup diagnostic still received no ACK. Visible OLED output and audible amplifier output remain manual checks.
- The user confirmed that a fresh recording from `/mic/test` played back speech successfully.

## 2026-11-10 — MAX98357A speaker output

- Checked the current firmware pin map. GPIO41, GPIO42 and GPIO14 were free and now carry BCLK, LRCLK and amplifier DIN respectively. See [HARDWARE.md](HARDWARE.md) for wiring, the SD_MODE check and the PCM HTTP contract.
- SpeakerManager initializes Philips I2S TX independently and duplicates each 16 kHz PCM16 mono sample into both slots. The asynchronous HTTP worker buffers one clip in PSRAM, applies per-request volume and holds the speaker through DMA drain.
- Built with ESP-IDF 5.5.2 and flashed only the factory application at `0x10000`. Esptool verified the written image. Serial confirmed the I2S configuration, a 16,000-frame 440 Hz test tone at 20% volume, and completed raw PCM and Piper jobs without a reported underrun.
- The live device returned 202 for both audio routes, 409 during upload and playback, 400 for incomplete sample framing or invalid volume, 408 for a stalled upload, 413 for excess length and 415 for an invalid media type. Health, device info, JPEG capture and MJPEG responded over Wi-Fi. MJPEG continued during PCM playback. The OLED scan ran on GPIO1/GPIO2 and reported no ACK at either configured display address.
- The server client converted a real Czech Piper WAV from 22.05 kHz to 16 kHz and received HTTP 202 from the ESP32. All 35 focused .NET tests passed, including the 16 kHz passthrough and 22.05 kHz conversion cases.
- Audible output still requires confirmation. The user measured 0.42 V on the installed module's SD pin, within the MAX98357A stereo-average enable range.
- A temporary ESP32-S3 pulse-counter build measured about 512 kHz on GPIO41 (BCLK) and 16 kHz on GPIO42 (LRC). GPIO14 (DIN) produced 95,665 rising edges during a 50% volume test and zero while muted. These readings are at the ESP32 GPIO input path. The amplifier-side signals and speaker output still need a physical check. Restored the original application with flash hash verification and confirmed HTTP health and test tone completion after reboot.

## 2026-11-09 — ESP32-S3 OLED bring-up

The startup diagnostic scans unreserved 7-bit addresses on I2C0 at 100 kHz.
It logs each responding address. At the first ACK from `0x3C` or `0x3D`,
the SH1106 initialization sequence enables the onboard DC-DC converter and
uses the controller's `0xA5` command to turn on all pixels without writing
display RAM. The diagnostic reports I2C failures and startup continues.
The OV5640 stays on I2C1. [SH1106 datasheet](https://buydisplay.com/download/ic/SH1106.pdf)

| OLED pin | ESP32-S3 connection |
| --- | --- |
| GND | GND |
| VCC | 3V3 |
| SCL (SCK) | GPIO47 |
| SDA | GPIO21 |

ESP-IDF v5.5.2 built the firmware. Esptool wrote and verified only the factory
application at `0x10000`, preserving the existing Wi-Fi provisioning partition.
The serial monitor at 115200 baud captured the following result after reset.

```text
I (1778) oled: Scanning I2C0 at 100 kHz (SDA GPIO21, SCL GPIO47)
W (1788) oled: OLED initialization skipped: no ACK at 0x3C or 0x3D; check OLED wiring
```

No address responded on I2C0. The camera's `0x3C` response was on I2C1.
Check the temporary unsoldered connections against the table, then reset to
repeat the scan. Once the module responds, inspect the entire panel for light.
The controller type and pixel output remain unverified. If the module
acknowledges the commands and stays dark, confirm the controller type before
testing the SSD1306 charge-pump initialization.

The OV5640 reported ID `0x5640` and captured a 640 × 480 JPEG at boot.
Wi-Fi acquired a DHCP address. Live requests verified `/device/info` and
`/health`, plus a 640 × 480 JPEG from both `/camera/stream` and
`/camera/capture`. The health and snapshot requests succeeded while the
MJPEG stream was active.

Three repeat USB resets and serial checks produced the same I2C0 result: no
device responded. The camera captured a VGA JPEG and Wi-Fi obtained a DHCP
address on each boot.

### Alternate I2C pins

The firmware now uses GPIO1 for SDA and GPIO2 for SCL on I2C0. These
header pins are available alongside the camera interface on the
[carrier pinout](https://github.com/profharris/GOOUUU_ESP32-S3-CAM/blob/main/README.md).
After rewiring, ESP-IDF v5.5.2 built the firmware and esptool verified the
application-only write. The serial log confirmed a scan at 100 kHz on GPIO1
and GPIO2. No I2C0 address responded. The SH1106 test was skipped. The OV5640
captured a 640 × 480 JPEG and Wi-Fi acquired a DHCP address. `/health`
returned HTTP 200 with `cameraReady: true`.

On two subsequent USB resets, the GPIO1/GPIO2 scan stopped at its first probe
(`0x08`) with `ESP_ERR_TIMEOUT`. Neither scan reached the OLED addresses.
The camera captured a VGA JPEG and Wi-Fi acquired a DHCP address on both boots.

The next diagnostic reset completed the GPIO1/GPIO2 scan with no ACK and
no timeout. Camera and Wi-Fi started. The physical wire state was unconfirmed.

The following reset, after clarifying that the module labels its clock pin
`SCK` (GPIO2), again timed out at `0x08`. Camera and Wi-Fi started.

Three consecutive resets completed the GPIO1/GPIO2 scan without a timeout or
any responding address. Camera and Wi-Fi started on each boot.

## 2026-11-10 — OLED detected

After a USB reset, the I2C0 scan at 100 kHz on GPIO1 (SDA) and GPIO2 (SCK)
detected the OLED at `0x3C`. The module acknowledged the SH1106 initialization
and all-pixels-on command. The user confirmed that the display lit up.
The OV5640 captured a 640 × 480 JPEG and Wi-Fi acquired a DHCP address.

### OLED startup face

The firmware replaces the full-panel test with a static 128 × 64 monochrome
smiley based on the reference image. It writes all eight SH1106 display RAM
pages, with a two-column offset for the visible area, before enabling output.
ESP-IDF 5.5.2 built the firmware and esptool verified the application-only
write. The serial monitor confirmed an ACK for the smiley frame at `0x3C`,
an OV5640 VGA JPEG capture and Wi-Fi startup. The rendered smiley awaits
visual confirmation.

### OLED face animation

A dedicated FreeRTOS task moves the eyes side to side and varies the width
and curvature of the smile. The task starts a frame every 160 ms and repeats
the motion every 32 frames. A failed transfer ends the animation and releases
I2C0. The ESP-IDF build and application-only flash succeeded. The serial
monitor confirmed that the OLED at `0x3C` acknowledged initialization and the
first animated update. The OV5640 captured a VGA JPEG and Wi-Fi acquired an
address. Visible motion awaits visual confirmation.
