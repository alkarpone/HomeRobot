# ESP32-S3 OLED I2C diagnostic

This standalone ESP-IDF v5.5.2 project scans I2C0 on SDA GPIO1 and SCL GPIO2. It
sends START, the 7-bit address with the write bit, then STOP. Each pass covers
0x08–0x77 with a 50 ms transaction timeout. The diagnostic sends no display
commands and starts no camera or Wi-Fi services.

The legacy I2C driver is deliberate. In ESP-IDF v5.5.2, the newer
`i2c_master_probe()` sets the bus to 100 kHz internally. The legacy command
link uses the configured 10, 50 or 100 kHz bus timing. Its address-only
transaction reports ACK (`ESP_OK`), NACK (`ESP_FAIL`) or timeout
(`ESP_ERR_TIMEOUT`). Keep the two I2C drivers in separate applications.
The scanner prints the GPIO matrix configuration, including open-drain mode,
input and output enable, internal pull-ups and idle logic levels. Idle levels
do not measure the external pull-ups or the rise time of either signal.

The [serial capture from the HomeRobot board](scan-serial.log) recorded 112
NACKs at each rate, with zero ACKs, timeouts or other errors.

## Run on the HomeRobot board

Activate ESP-IDF using the [camera project setup](../README.md#build-and-flash-on-macos).
From this directory, with the correct serial port and a verified factory app
partition at `0x10000`:

```sh
port=/dev/cu.usbmodem5CE51304931
camera_app=../build/homerobot_camera.bin
python -m esptool --chip esp32s3 -p "$port" verify_flash 0x10000 "$camera_app"
IDF_COMPONENT_MANAGER=0 idf.py build
python -m esptool --chip esp32s3 -p "$port" write_flash 0x10000 build/oled_i2c_diagnostic.bin
idf.py -p "$port" monitor
```

If verification fails, obtain a matching image or back up the existing app
partition before writing the diagnostic. Use application-only writes on the
HomeRobot board to preserve its bootloader, partition table and Wi-Fi NVS.
The scanner runs once per reset. The console runs at 115200 baud on UART0
and is mirrored to native USB Serial/JTAG.

Exit the monitor with **Ctrl+]** and restore the verified camera image:

```sh
python -m esptool --chip esp32s3 -p "$port" write_flash 0x10000 "$camera_app"
```
