#include "oled.h"

#include "driver/i2c_master.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "oled";
static const int TIMEOUT_MS = 50;

static esp_err_t scan_bus(i2c_master_bus_handle_t bus, uint8_t *oled_address)
{
    // Scan the unreserved 7-bit address range, excluding the camera's separate bus.
    for (uint8_t address = 0x08; address <= 0x77; ++address) {
        esp_err_t err = i2c_master_probe(bus, address, TIMEOUT_MS);
        if (err == ESP_ERR_NOT_FOUND) {
            continue;
        }
        if (err != ESP_OK) {
            ESP_LOGE(TAG, "I2C0 scan failed at 0x%02X: %s", address, esp_err_to_name(err));
            return err;
        }
        ESP_LOGI(TAG, "I2C0 device detected at 0x%02X", address);
        if (*oled_address == 0 && (address == 0x3C || address == 0x3D)) {
            *oled_address = address;
        }
    }
    return ESP_OK;
}

enum {
    OLED_WIDTH = 128,
    OLED_HEIGHT = 64,
    SH1106_RAM_WIDTH = 132,
    SH1106_COLUMN_OFFSET = (SH1106_RAM_WIDTH - OLED_WIDTH) / 2,
    FACE_HALF_CYCLE = 16,
};

static bool face_pixel(int x, int y, int gaze, int smile)
{
    int eye_x = x - (x < OLED_WIDTH / 2 ? 40 : 87);
    if (y >= 7 && y <= 8 && eye_x >= -7 && eye_x <= 7) {
        return true;
    }

    eye_x -= gaze;
    int eye_y = y - 24;
    if (eye_x * eye_x * 121 + eye_y * eye_y * 100 <= 12100) {
        return true;
    }

    int smile_x = x - 63;
    int half_width = 32 + smile / 2;
    int width_squared = half_width * half_width;
    int depth = 6 + smile / 2;
    int smile_y = 54 - (depth * smile_x * smile_x + width_squared / 2) / width_squared;
    return smile_x >= -half_width && smile_x <= half_width &&
           y >= smile_y - 1 && y <= smile_y + 1;
}

static int face_wave(int phase)
{
    phase %= 2 * FACE_HALF_CYCLE;
    return phase <= FACE_HALF_CYCLE ? phase : 2 * FACE_HALF_CYCLE - phase;
}

static esp_err_t sh1106_write_face(i2c_master_dev_handle_t device, int phase)
{
    int gaze = face_wave(phase) - FACE_HALF_CYCLE / 2;
    int smile = face_wave(phase + FACE_HALF_CYCLE / 2);
    for (int page = 0; page < OLED_HEIGHT / 8; ++page) {
        // Select this RAM page and reset the column address to zero.
        const uint8_t select_page[] = {0x00, 0xB0 | page, 0x00, 0x10};
        esp_err_t err = i2c_master_transmit(device, select_page, sizeof(select_page), TIMEOUT_MS);
        if (err != ESP_OK) {
            return err;
        }

        // Clear the unused columns on both sides while writing the visible area.
        uint8_t data[1 + SH1106_RAM_WIDTH] = {0x40};
        for (int x = 0; x < OLED_WIDTH; ++x) {
            for (int bit = 0; bit < 8; ++bit) {
                if (face_pixel(x, page * 8 + bit, gaze, smile)) {
                    data[1 + SH1106_COLUMN_OFFSET + x] |= 1U << bit;
                }
            }
        }
        err = i2c_master_transmit(device, data, sizeof(data), TIMEOUT_MS);
        if (err != ESP_OK) {
            return err;
        }
    }
    return ESP_OK;
}

static esp_err_t sh1106_animate(i2c_master_dev_handle_t device, uint8_t address)
{
    TickType_t frame_start = xTaskGetTickCount();
    bool first_frame = true;
    for (int phase = 1; ; phase = (phase + 1) % (2 * FACE_HALF_CYCLE)) {
        vTaskDelayUntil(&frame_start, pdMS_TO_TICKS(160));
        esp_err_t err = sh1106_write_face(device, phase);
        if (err != ESP_OK) {
            return err;
        }
        if (first_frame) {
            ESP_LOGI(TAG, "SH1106 initialization and animated smiley frames acknowledged at 0x%02X",
                     address);
            first_frame = false;
        }
    }
}

static void sh1106_show_face(i2c_master_bus_handle_t bus, uint8_t address)
{
    const i2c_device_config_t config = {
        .dev_addr_length = I2C_ADDR_BIT_LEN_7,
        .device_address = address,
        .scl_speed_hz = 100000,
    };
    const uint8_t commands[] = {
        0x00,       // I2C command stream
        0xAE,       // Display off during setup
        0xD5, 0x50, // Display clock
        0xA8, 0x3F, // Multiplex across 64 rows
        0xD3, 0x00, // Display offset
        0x40,       // Start line 0
        0xDA, 0x12, // Alternate COM pin configuration
        0xA1,       // Segment remap
        0xC8,       // Reverse COM scan direction
        0x81, 0x40, // Moderate contrast
        0xAD, 0x8B, // Enable the SH1106 DC-DC converter
        0xA4,       // Display pixels from RAM
        0xA6,       // Normal polarity
    };
    i2c_master_dev_handle_t device;
    esp_err_t err = i2c_master_bus_add_device(bus, &config, &device);
    if (err == ESP_OK) {
        err = i2c_master_transmit(device, commands, sizeof(commands), TIMEOUT_MS);
        if (err == ESP_OK) {
            err = sh1106_write_face(device, 0);
        }
        if (err == ESP_OK) {
            const uint8_t display_on[] = {0x00, 0xAF};
            err = i2c_master_transmit(device, display_on, sizeof(display_on), TIMEOUT_MS);
        }
        if (err == ESP_OK) {
            err = sh1106_animate(device, address);
        }
        i2c_master_bus_rm_device(device);
    }
    ESP_LOGE(TAG, "SH1106 initialization or smiley animation failed at 0x%02X: %s",
             address, esp_err_to_name(err));
}

static void oled_run_face(void)
{
    const i2c_master_bus_config_t config = {
        .i2c_port = I2C_NUM_0,
        .sda_io_num = GPIO_NUM_1,
        .scl_io_num = GPIO_NUM_2,
        .clk_source = I2C_CLK_SRC_DEFAULT,
        .glitch_ignore_cnt = 7,
        .flags.enable_internal_pullup = true,
    };
    i2c_master_bus_handle_t bus;
    esp_err_t err = i2c_new_master_bus(&config, &bus);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "I2C0 initialization failed: %s", esp_err_to_name(err));
        return;
    }

    ESP_LOGI(TAG, "Scanning I2C0 at 100 kHz (SDA GPIO%d, SCL GPIO%d)",
             config.sda_io_num, config.scl_io_num);
    uint8_t address = 0;
    if (scan_bus(bus, &address) == ESP_OK) {
        if (address == 0) {
            ESP_LOGW(TAG, "OLED initialization skipped: no ACK at 0x3C or 0x3D; check OLED wiring");
        } else {
            sh1106_show_face(bus, address);
        }
    }
    i2c_del_master_bus(bus);
}

static void oled_face_task(void *arg)
{
    (void)arg;
    oled_run_face();
    vTaskDelete(NULL);
}

void oled_show_startup_face(void)
{
    if (xTaskCreate(oled_face_task, "oled_face", 3072, NULL, 1, NULL) != pdPASS) {
        ESP_LOGE(TAG, "Failed to start OLED animation task");
    }
}
