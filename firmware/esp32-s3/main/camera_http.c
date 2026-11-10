#include "camera_http.h"
#include "microphone.h"
#include "speaker_manager.h"

#include <stdio.h>

#include "cJSON.h"
#include "esp_app_desc.h"
#include "esp_camera.h"
#include "esp_chip_info.h"
#include "esp_flash.h"
#include "esp_http_server.h"
#include "esp_log.h"
#include "esp_psram.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "lwip/sockets.h"
#include "sensor.h"

#define STREAM_BOUNDARY "homerobot-frame"

static const char *TAG = "camera";
static esp_err_t camera_status = ESP_ERR_INVALID_STATE;
static uint16_t sensor_pid;
static SemaphoreHandle_t stream_slot;

static camera_fb_t *get_jpeg(void)
{
    camera_fb_t *frame = esp_camera_fb_get();
    if (frame && (frame->format != PIXFORMAT_JPEG || frame->len < 4 ||
                  frame->buf[0] != 0xff || frame->buf[1] != 0xd8 ||
                  frame->buf[frame->len - 2] != 0xff || frame->buf[frame->len - 1] != 0xd9)) {
        esp_camera_fb_return(frame);
        return NULL;
    }
    return frame;
}

esp_err_t camera_init(void)
{
    // Dratek N16R8 DVP board. RESET and PWDN are strapped on the carrier.
    const camera_config_t config = {
        .pin_pwdn = -1,
        .pin_reset = -1,
        .pin_xclk = 15,
        .pin_sccb_sda = 4,
        .pin_sccb_scl = 5,
        .pin_d0 = 11,
        .pin_d1 = 9,
        .pin_d2 = 8,
        .pin_d3 = 10,
        .pin_d4 = 12,
        .pin_d5 = 18,
        .pin_d6 = 17,
        .pin_d7 = 16,
        .pin_vsync = 6,
        .pin_href = 7,
        .pin_pclk = 13,
        .xclk_freq_hz = 20000000,
        .ledc_timer = LEDC_TIMER_0,
        .ledc_channel = LEDC_CHANNEL_0,
        .pixel_format = PIXFORMAT_JPEG,
        .frame_size = FRAMESIZE_VGA,
        .jpeg_quality = 12,
        .fb_count = 2,
        .fb_location = CAMERA_FB_IN_PSRAM,
        .grab_mode = CAMERA_GRAB_LATEST,
    };

    camera_status = esp_camera_init(&config);
    if (camera_status != ESP_OK) {
        return camera_status;
    }

    sensor_t *sensor = esp_camera_sensor_get();
    sensor_pid = sensor ? sensor->id.PID : 0;
    if (sensor_pid != OV5640_PID) {
        ESP_LOGE(TAG, "Expected OV5640, got sensor PID 0x%04x", sensor_pid);
        esp_camera_deinit();
        return camera_status = ESP_ERR_NOT_SUPPORTED;
    }

    // Correct the horizontal orientation of the Dratek OV5640 module.
    if (sensor->set_hmirror(sensor, 1) != 0) {
        ESP_LOGE(TAG, "Could not set the camera orientation");
        esp_camera_deinit();
        return camera_status = ESP_FAIL;
    }

    camera_fb_t *frame = get_jpeg();
    if (!frame) {
        esp_camera_deinit();
        return camera_status = ESP_ERR_TIMEOUT;
    }
    ESP_LOGI(TAG, "OV5640 (PID 0x%04x) captured %ux%u JPEG (%u bytes)",
             sensor_pid, (unsigned)frame->width, (unsigned)frame->height, (unsigned)frame->len);
    esp_camera_fb_return(frame);
    return ESP_OK;
}

static esp_err_t unavailable(httpd_req_t *req, const char *message)
{
    httpd_resp_set_status(req, "503 Service Unavailable");
    httpd_resp_set_type(req, "text/plain");
    httpd_resp_set_hdr(req, "Cache-Control", "no-store");
    return httpd_resp_sendstr(req, message);
}

static esp_err_t health(httpd_req_t *req)
{
    char body[160];
    bool ready = camera_status == ESP_OK;
    snprintf(body, sizeof(body),
             "{\"status\":\"%s\",\"cameraReady\":%s,\"cameraStatus\":\"%s\",\"uptimeSeconds\":%lld}",
             ready ? "ok" : "degraded", ready ? "true" : "false", esp_err_to_name(camera_status),
             (long long)(esp_timer_get_time() / 1000000));
    if (!ready) {
        httpd_resp_set_status(req, "503 Service Unavailable");
    }
    httpd_resp_set_type(req, "application/json");
    httpd_resp_set_hdr(req, "Cache-Control", "no-store");
    return httpd_resp_sendstr(req, body);
}

static esp_err_t device_info(httpd_req_t *req)
{
    esp_chip_info_t chip;
    esp_chip_info(&chip);
    char revision[12];
    snprintf(revision, sizeof(revision), "%u.%u", chip.revision / 100, chip.revision % 100);
    uint32_t flash_size = 0;
    esp_flash_get_physical_size(NULL, &flash_size);
    const esp_app_desc_t *app = esp_app_get_description();

    cJSON *info = cJSON_CreateObject();
    if (!info ||
        !cJSON_AddStringToObject(info, "board", "Dratek 1786001451 ESP32-S3 N16R8") ||
        !cJSON_AddStringToObject(info, "chip", "ESP32-S3") ||
        !cJSON_AddStringToObject(info, "revision", revision) ||
        !cJSON_AddNumberToObject(info, "cores", chip.cores) ||
        !cJSON_AddNumberToObject(info, "flashBytes", flash_size) ||
        !cJSON_AddNumberToObject(info, "psramBytes", esp_psram_get_size()) ||
        !cJSON_AddStringToObject(info, "sensor", sensor_pid == OV5640_PID ? "OV5640" : "unknown") ||
        !cJSON_AddNumberToObject(info, "sensorPid", sensor_pid) ||
        !cJSON_AddStringToObject(info, "firmware", app->version) ||
        !cJSON_AddStringToObject(info, "idfVersion", app->idf_ver)) {
        cJSON_Delete(info);
        return httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, NULL);
    }
    char *body = cJSON_PrintUnformatted(info);
    cJSON_Delete(info);
    if (!body) {
        return httpd_resp_send_err(req, HTTPD_500_INTERNAL_SERVER_ERROR, NULL);
    }
    httpd_resp_set_type(req, "application/json");
    esp_err_t err = httpd_resp_sendstr(req, body);
    cJSON_free(body);
    return err;
}

static esp_err_t capture(httpd_req_t *req)
{
    if (camera_status != ESP_OK) {
        return unavailable(req, "Camera unavailable");
    }
    camera_fb_t *frame = get_jpeg();
    if (!frame) {
        return unavailable(req, "JPEG capture failed");
    }
    httpd_resp_set_type(req, "image/jpeg");
    httpd_resp_set_hdr(req, "Cache-Control", "no-store");
    esp_err_t err = httpd_resp_send(req, (const char *)frame->buf, frame->len);
    esp_camera_fb_return(frame);
    return err;
}

static esp_err_t send_mjpeg_frame(httpd_req_t *req, const camera_fb_t *frame)
{
    char header[128];
    int length = snprintf(header, sizeof(header),
                          "--" STREAM_BOUNDARY "\r\nContent-Type: image/jpeg\r\n"
                          "Content-Length: %u\r\n\r\n", (unsigned)frame->len);
    esp_err_t err = httpd_resp_send_chunk(req, header, length);
    if (err == ESP_OK) {
        err = httpd_resp_send_chunk(req, (const char *)frame->buf, frame->len);
    }
    if (err == ESP_OK) {
        err = httpd_resp_send_chunk(req, "\r\n", 2);
    }
    return err;
}

static void stream_frames(void *arg)
{
    httpd_req_t *req = arg;
    httpd_resp_set_type(req, "multipart/x-mixed-replace; boundary=" STREAM_BOUNDARY);
    httpd_resp_set_hdr(req, "Cache-Control", "no-store");

    bool sent_frame = false;
    esp_err_t err = ESP_OK;
    while (err == ESP_OK) {
        camera_fb_t *frame = get_jpeg();
        if (!frame) {
            if (sent_frame) {
                err = httpd_resp_sendstr_chunk(req, "--" STREAM_BOUNDARY "--\r\n");
                if (err == ESP_OK) {
                    err = httpd_resp_send_chunk(req, NULL, 0);
                }
            } else {
                err = unavailable(req, "JPEG capture failed");
            }
            break;
        }
        err = send_mjpeg_frame(req, frame);
        esp_camera_fb_return(frame);
        sent_frame = true;
    }
    if (err != ESP_OK) {
        shutdown(httpd_req_to_sockfd(req), SHUT_RDWR);
    }
    httpd_req_async_handler_complete(req);
    xSemaphoreGive(stream_slot);
    vTaskDelete(NULL);
}

static esp_err_t stream(httpd_req_t *req)
{
    if (camera_status != ESP_OK) {
        return unavailable(req, "Camera unavailable");
    }
    if (xSemaphoreTake(stream_slot, 0) != pdTRUE) {
        return unavailable(req, "MJPEG stream already in use");
    }

    httpd_req_t *async_req;
    if (httpd_req_async_handler_begin(req, &async_req) != ESP_OK) {
        xSemaphoreGive(stream_slot);
        return unavailable(req, "Cannot start MJPEG stream");
    }
    if (xTaskCreate(stream_frames, "mjpeg", 4096, async_req, 4, NULL) != pdPASS) {
        unavailable(async_req, "Cannot start MJPEG stream");
        httpd_req_async_handler_complete(async_req);
        xSemaphoreGive(stream_slot);
    }
    return ESP_OK;
}

esp_err_t camera_http_start(void)
{
    stream_slot = xSemaphoreCreateBinary();
    if (!stream_slot) {
        return ESP_ERR_NO_MEM;
    }
    xSemaphoreGive(stream_slot);

    httpd_config_t config = HTTPD_DEFAULT_CONFIG();
    config.recv_wait_timeout = 3;
    config.send_wait_timeout = 3;
    config.lru_purge_enable = true;

    httpd_handle_t server;
    esp_err_t err = httpd_start(&server, &config);
    if (err != ESP_OK) {
        vSemaphoreDelete(stream_slot);
        return err;
    }
    const httpd_uri_t routes[] = {
        { .uri = "/health", .method = HTTP_GET, .handler = health },
        { .uri = "/camera/capture", .method = HTTP_GET, .handler = capture },
        { .uri = "/camera/stream", .method = HTTP_GET, .handler = stream },
        { .uri = "/device/info", .method = HTTP_GET, .handler = device_info },
    };
    for (size_t i = 0; i < sizeof(routes) / sizeof(routes[0]); i++) {
        err = httpd_register_uri_handler(server, &routes[i]);
        if (err != ESP_OK) {
            httpd_stop(server);
            vSemaphoreDelete(stream_slot);
            return err;
        }
    }
    err = speaker_http_register(server);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "Cannot register audio routes: %s", esp_err_to_name(err));
    }
    err = microphone_http_register(server);
    if (err != ESP_OK) {
        ESP_LOGE(TAG, "Cannot register microphone routes: %s", esp_err_to_name(err));
    }
    return ESP_OK;
}
