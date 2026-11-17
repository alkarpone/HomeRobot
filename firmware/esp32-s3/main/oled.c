#include "oled.h"
#include "speaker_manager.h"
#include <string.h>
#include <stdio.h>
#include "esp_timer.h"
#include "esp_http_server.h"
#include "cJSON.h"

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
};

typedef enum { IDLE, LISTENING, THINKING, SPEAKING, HAPPY, SAD, ERROR_FACE, ANGRY, SURPRISED, EYES_CLOSED } face_expression_t;
static const char *expressions[] = {"idle", "listening", "thinking", "speaking", "happy", "sad", "error", "angry", "surprised", "eyes_closed"};
static portMUX_TYPE face_lock = portMUX_INITIALIZER_UNLOCKED;
static face_expression_t selected_face = IDLE;
static int64_t blink_until;
static bool display_ready;

static esp_err_t face_get(httpd_req_t *req)
{
    portENTER_CRITICAL(&face_lock);
    face_expression_t face = selected_face;
    bool ready = display_ready;
    portEXIT_CRITICAL(&face_lock);
    if (!ready) {
        httpd_resp_set_status(req, "503 Service Unavailable");
        return httpd_resp_sendstr(req, "OLED unavailable");
    }
    char body[64];
    snprintf(body, sizeof(body), "{\"expression\":\"%s\"}", expressions[face]);
    httpd_resp_set_type(req, "application/json");
    httpd_resp_set_hdr(req, "Cache-Control", "no-store");
    return httpd_resp_sendstr(req, body);
}

static esp_err_t face_post(httpd_req_t *req)
{
    if (req->content_len <= 0 || req->content_len >= 96)
        return httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "Expected expression JSON");
    char body[96] = {0};
    int received = 0;
    while (received < req->content_len) {
        int count = httpd_req_recv(req, body + received, req->content_len - received);
        if (count <= 0) return httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "Incomplete body");
        received += count;
    }
    const char *end = NULL;
    cJSON *json = cJSON_ParseWithLengthOpts(body, received + 1, &end, true);
    cJSON *value = cJSON_GetObjectItemCaseSensitive(json, "expression");
    int face = -1;
    bool blink = false;
    if (cJSON_IsObject(json) && cJSON_GetArraySize(json) == 1 && cJSON_IsString(value)) {
        for (int i = 0; i < sizeof(expressions) / sizeof(expressions[0]); ++i)
            if (strcmp(value->valuestring, expressions[i]) == 0) face = i;
        blink = strcmp(value->valuestring, "blink") == 0;
    }
    cJSON_Delete(json);
    if (face < 0 && !blink) return httpd_resp_send_err(req, HTTPD_400_BAD_REQUEST, "Unsupported expression");
    portENTER_CRITICAL(&face_lock);
    bool ready = display_ready;
    if (ready) {
        if (blink) blink_until = esp_timer_get_time() + 180000;
        else { selected_face = face; blink_until = 0; }
    }
    portEXIT_CRITICAL(&face_lock);
    if (!ready) {
        httpd_resp_set_status(req, "503 Service Unavailable");
        return httpd_resp_sendstr(req, "OLED unavailable");
    }
    httpd_resp_set_status(req, "202 Accepted");
    httpd_resp_set_type(req, "application/json");
    return httpd_resp_sendstr(req, "{\"status\":\"accepted\"}");
}

esp_err_t oled_http_register(httpd_handle_t server)
{
    const httpd_uri_t get = {.uri = "/face", .method = HTTP_GET, .handler = face_get};
    const httpd_uri_t post = {.uri = "/face", .method = HTTP_POST, .handler = face_post};
    esp_err_t err = httpd_register_uri_handler(server, &get);
    return err == ESP_OK ? httpd_register_uri_handler(server, &post) : err;
}

static bool face_pixel(int x, int y, int gaze, int opening)
{
    face_expression_t face = gaze;
    int side = x < OLED_WIDTH / 2 ? -1 : 1;
    int ex = x - (side < 0 ? 40 : 87);
    int ey = y - 25;
    bool closed = face == EYES_CLOSED;
    int height = face == SURPRISED ? 14 : (face == ANGRY || face == HAPPY || face == SAD || face == ERROR_FACE ? 6 : 11);
    if (closed ? (ex >= -10 && ex <= 10 && ey >= 0 && ey <= 2) :
        (ex * ex * height * height + ey * ey * 100 <= 100 * height * height)) return true;
    int brow = 8;
    if (face == ANGRY || face == ERROR_FACE) brow -= side * ex / 2;
    if (face == SAD) brow += side * ex / 2;
    if (ex >= -12 && ex <= 12 && y >= brow && y <= brow + 1) return true;
    int mx = x - 63;
    if (opening > 0) {
        int my = y - 50;
        int width = 8 + opening * 3;
        int height = opening * 3;
        return mx * mx * height * height + my * my * width * width <= width * width * height * height;
    }
    int mouth_y = 49;
    if (face == HAPPY) mouth_y = 54 - mx * mx / 100;
    if (face == SAD || face == ANGRY || face == ERROR_FACE) mouth_y = 46 + mx * mx / 100;
    return mx >= -23 && mx <= 23 && y >= mouth_y && y <= mouth_y + 1;
}

static esp_err_t sh1106_write_face(i2c_master_dev_handle_t device, int phase, unsigned opening)
{
    int gaze = phase;
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
                if (face_pixel(x, page * 8 + bit, gaze, opening)) {
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
    (void)address;
    int previous = -1;
    unsigned previous_opening = 0;
    portENTER_CRITICAL(&face_lock);
    display_ready = true;
    portEXIT_CRITICAL(&face_lock);
    for (;;) {
        portENTER_CRITICAL(&face_lock);
        int face = esp_timer_get_time() < blink_until ? EYES_CLOSED : selected_face;
        portEXIT_CRITICAL(&face_lock);
        unsigned opening = speaker_mouth_opening();
        if (face != previous || opening != previous_opening) {
            esp_err_t err = sh1106_write_face(device, face, opening);
            if (err != ESP_OK) {
                portENTER_CRITICAL(&face_lock);
                display_ready = false;
                portEXIT_CRITICAL(&face_lock);
                return err;
            }
            previous = face;
            previous_opening = opening;
        }
        vTaskDelay(pdMS_TO_TICKS(20));
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
            err = sh1106_write_face(device, 0, 0);
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
    ESP_LOGE(TAG, "SH1106 initialization or face update failed at 0x%02X: %s",
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
