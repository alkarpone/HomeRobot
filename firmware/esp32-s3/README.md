# HomeRobot ESP32-S3 camera

This ESP-IDF project runs on the [Dratek ESP32-S3 N16R8 with OV5640](https://dratek.cz/arduino-platforma/232702-vyvojova-deska-esp32-s3-s-kameru-ov5640-wifi-bluetooth-nespajeny-s-pinovou-listou-v-baleni.html), SKU 1786001451. It uses 16 MiB quad flash in DIO mode, 8 MiB octal PSRAM and a UART0 console mirrored to native USB Serial/JTAG. The ESP32-S3 joins a 2.4 GHz WPA2/WPA3 personal network as a station.

## Camera wiring

The N16R8 carrier uses an 8-bit DVP interface and SCCB. This map follows the [dual-USB N16R8 board pinout](https://github.com/profharris/GOOUUU_ESP32-S3-CAM/blob/main/README.md) and the [OV5640 DVP connector reference](https://github.com/dunknowcoding/NiusCam/blob/main/docs/HARDWARE.md#supported-24-pin-ov5640-dvp-layout). The [ESP32-S3-EYE schematic](https://dl.espressif.com/dl/schematics/SCH_ESP32-S3-EYE-MB_20211201_V2.2.pdf) documents the same camera signal routing for comparison. The supplied Dratek board pinout confirms all the routed camera GPIOs below. The diagram's RST label refers to the ESP32 reset pin. The camera RESET and PWDN signals have no assigned GPIO in the firmware. The connected Dratek board reported sensor ID `0x5640` and captured a 640 × 480 JPEG with the mapping below.

| Signal | ESP32-S3 GPIO |
| --- | --- |
| XCLK (20 MHz) | 15 |
| SCCB SDA / SCL | 4 / 5 |
| VSYNC / HREF / PCLK | 6 / 7 / 13 |
| D0–D7 (sensor Y2–Y9, in order) | 11, 9, 8, 10, 12, 18, 17, 16 |
| RESET / PWDN | Carrier straps, no firmware GPIO |

Firmware version 0.1.0 uses sensor-native VGA (640 × 480) JPEG for both endpoints and keeps two frame buffers in PSRAM. It checks the OV5640 sensor ID and acquires a JPEG at boot, even on an unprovisioned board.

## Build and flash on macOS

Use [ESP-IDF v5.5.2](https://github.com/espressif/esp-idf/releases/tag/v5.5.2) with Python 3.13, CMake and Ninja. This Mac has an isolated SDK, tools and Python under `~/.espressif/home-robot`. For a new installation, clone the v5.5.2 tag with its submodules and run `./install.sh esp32s3` with the desired Python on PATH. The ESP-IDF component manager fetches the locked camera dependencies on the first build.

From this directory on the configured Mac:

```sh
esp_setup="$HOME/.espressif/home-robot"
export IDF_TOOLS_PATH="$esp_setup/tools"
export PATH="$IDF_TOOLS_PATH/python/cpython-3.13.18-macos-aarch64-none/bin:$PATH"
. "$esp_setup/esp-idf/export.sh"

port=/dev/cu.usbmodem5CE51304931
idf.py build
idf.py -p "$port" flash
```

Connect the board via its UART USB-C port for the full boot log. The native USB/OTG port also carries application logs. Substitute the serial path if it changes. Use the USB log with `idf.py -p "$port" monitor` and exit the monitor with **Ctrl+]** before flashing or provisioning.

## Wi-Fi provisioning

Create `.env` in this firmware directory with permissions `600`. Use literal, unquoted values after `WIFI_SSID=` and `WIFI_PASSWORD=`, one entry per line. Git ignores this file. The provisioning script reads it as data and writes only the `wifi_cfg` NVS partition. Reflash the application as usual to preserve credentials, or rerun provisioning to replace them. The development board stores this partition in plaintext flash.

```sh
python provision_wifi.py --port "$port"
idf.py -p "$port" monitor
```

For an ignored `wifi.local.json` file with string fields `ssid` and `password`, run
`python provision_wifi.py --port "$port" --config wifi.local.json`. The config option also accepts an absolute path.

The serial log prints the DHCP address as soon as the board joins the network. On a trusted LAN, query the device from the same network:

```sh
base=http://DEVICE_IP
curl --fail "$base/health"
curl --fail "$base/device/info"
curl --fail "$base/camera/capture" -o capture.jpg
open capture.jpg
open "$base/camera/stream"
```

| GET route | Response |
| --- | --- |
| `/health` | JSON with camera startup status and uptime. A failed camera probe returns HTTP 503. |
| `/device/info` | JSON with chip, revision, flash, PSRAM, detected sensor and firmware versions. |
| `/camera/capture` | A JPEG snapshot. |
| `/camera/stream` | Multipart MJPEG for one viewer at a time. The stream runs independently of the status and snapshot handlers. |

This development HTTP server exposes the camera to peers on the trusted network without HTTP authentication or TLS.

## Server-driven Piper speech

P13-A extends this project with an optional authenticated WSS robot client and
bounded HTTPS PCM streaming through the existing SpeakerManager. Wi-Fi, GPIOs,
I2S initialization, test tone, camera, microphone and OLED retain their existing
implementations. Without provisioned server settings, existing HTTP operation
continues. See [server settings, application flash and physical verification](../../docs/ESP32_TTS.md).
