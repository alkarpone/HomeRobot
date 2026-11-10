"""Flash Wi-Fi credentials to the device's dedicated NVS partition."""

import argparse
import csv
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile


def read_credentials(path):
    if path.suffix == ".json":
        config = json.loads(path.read_text(encoding="utf-8-sig"))
        return config["ssid"].encode("utf-8"), config["password"].encode("utf-8")

    config = {}
    for line in path.read_bytes().splitlines():
        key, separator, value = line.partition(b"=")
        if separator and key in (b"WIFI_SSID", b"WIFI_PASSWORD"):
            config[key] = value
    return config[b"WIFI_SSID"], config[b"WIFI_PASSWORD"]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--port", required=True, help="ESP32-S3 serial port")
    project = Path(__file__).resolve().parent
    parser.add_argument("--config", type=Path, default=project / ".env",
                        help="local .env or JSON credential file (default: project .env)")
    args = parser.parse_args()

    idf = os.environ.get("IDF_PATH")
    if not idf:
        parser.error("activate ESP-IDF using export.sh first")
    try:
        ssid, password = read_credentials(args.config)
    except (OSError, ValueError, KeyError, TypeError, AttributeError):
        parser.error("use WIFI_SSID and WIFI_PASSWORD in .env or ssid and password in JSON")
    if not 1 <= len(ssid) <= 32 or not 8 <= len(password) <= 63 or b"\0" in ssid + password:
        parser.error("use an SSID of 1–32 bytes and a WPA2/WPA3 passphrase of 8–63 bytes")

    partitions = Path(idf) / "components/partition_table"
    partition_tool = [sys.executable, str(partitions / "parttool.py"), "--port", args.port]
    selection = ["--partition-name", "wifi_cfg"]
    partition_info = subprocess.check_output(
        [*partition_tool, "get_partition_info", *selection, "--info", "size"],
        text=True,
    ).splitlines()
    try:
        size = int(partition_info[-1], 16)
    except (IndexError, ValueError):
        parser.error("wifi_cfg partition missing; flash the firmware first")

    generator = Path(idf) / "components/nvs_flash/nvs_partition_generator/nvs_partition_gen.py"
    with tempfile.TemporaryDirectory(prefix="homerobot-wifi-") as temporary:
        source = Path(temporary) / "wifi.csv"
        image = Path(temporary) / "wifi.bin"
        with source.open("w", newline="") as file:
            writer = csv.writer(file)
            writer.writerow(["key", "type", "encoding", "value"])
            writer.writerow(["homerobot", "namespace", "", ""])
            writer.writerow(["ssid", "data", "hex2bin", ssid.hex()])
            writer.writerow(["password", "data", "hex2bin", password.hex()])
        subprocess.run(
            [sys.executable, str(generator), "generate", str(source), str(image), str(size)],
            check=True,
        )
        subprocess.run(
            [*partition_tool, "write_partition", *selection, "--input", str(image)],
            check=True,
        )


if __name__ == "__main__":
    main()
