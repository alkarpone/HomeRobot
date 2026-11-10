#include "microphone.h"

#include <math.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include "driver/gpio.h"
#include "driver/i2s_std.h"
#include "esp_attr.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include "lwip/sockets.h"

enum {
    SAMPLE_RATE = 16000,
    TEST_SAMPLES = 5 * SAMPLE_RATE,
    LEVEL_SAMPLES = SAMPLE_RATE / 4,
    WARMUP_SAMPLES = SAMPLE_RATE / 2,
    DMA_FRAMES = 256,
    MIC_BCLK = GPIO_NUM_47,
    MIC_WS = GPIO_NUM_21,
    MIC_DIN = GPIO_NUM_40, // Shared with microSD D0. Keep the slot empty.
};

typedef struct {
    int32_t left;
    int32_t right;
} i2s_frame_t;

typedef struct {
    size_t count;
    size_t zeros;
    size_t rails;
    int16_t min;
    int16_t max;
    unsigned peak;
    uint64_t sum_squares;
} mic_stats_t;

// RIFF PCM on the little-endian ESP32-S3.
typedef struct {
    char riff[4];
    uint32_t riff_size;
    char wave_fmt[8];
    uint32_t fmt_size;
    uint16_t format;
    uint16_t channels;
    uint32_t sample_rate;
    uint32_t byte_rate;
    uint16_t block_align;
    uint16_t bits_per_sample;
    char data[4];
    uint32_t data_size;
} wav_header_t;

_Static_assert(sizeof(wav_header_t) == 44, "Unexpected PCM WAV header size");

static const char *TAG = "microphone";
static i2s_chan_handle_t rx;
static SemaphoreHandle_t capture_slot;
static volatile bool rx_overflow;

static bool IRAM_ATTR on_rx_overflow(i2s_chan_handle_t channel, i2s_event_data_t *event, void *user)
{
    rx_overflow = true;
    return false;
}

esp_err_t microphone_init(void)
{
    capture_slot = xSemaphoreCreateBinary();
    if (!capture_slot) {
        return ESP_ERR_NO_MEM;
    }

    i2s_chan_config_t channel = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_1, I2S_ROLE_MASTER);
    channel.dma_desc_num = 8;
    channel.dma_frame_num = DMA_FRAMES;
    esp_err_t err = i2s_new_channel(&channel, NULL, &rx);
    const i2s_std_config_t config = {
        .clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(SAMPLE_RATE),
        // Read both slots explicitly and select the left channel in software.
        .slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_32BIT, I2S_SLOT_MODE_STEREO),
        .gpio_cfg = {
            .mclk = I2S_GPIO_UNUSED,
            .bclk = MIC_BCLK,
            .ws = MIC_WS,
            .dout = I2S_GPIO_UNUSED,
            .din = MIC_DIN,
        },
    };
    if (err == ESP_OK) {
        err = i2s_channel_init_std_mode(rx, &config);
    }
    if (err == ESP_OK) {
        err = gpio_pulldown_en(MIC_DIN);
    }
    if (err == ESP_OK) {
        const i2s_event_callbacks_t callbacks = { .on_recv_q_ovf = on_rx_overflow };
        err = i2s_channel_register_event_callback(rx, &callbacks, NULL);
    }
    if (err != ESP_OK) {
        if (rx) {
            i2s_del_channel(rx);
            rx = NULL;
        }
        vSemaphoreDelete(capture_slot);
        capture_slot = NULL;
        return err;
    }

    xSemaphoreGive(capture_slot);
    ESP_LOGI(TAG, "I2S1 RX ready: Philips stereo framing, left PCM, %d Hz, 32-bit slots, BCLK GPIO%d, WS GPIO%d, DIN GPIO%d",
             SAMPLE_RATE, MIC_BCLK, MIC_WS, MIC_DIN);
    return ESP_OK;
}

static esp_err_t capture_pcm(int16_t *pcm, size_t sample_count, mic_stats_t *stats)
{
    i2s_frame_t input[DMA_FRAMES];
    size_t warmup_left = WARMUP_SAMPLES;
    rx_overflow = false;
    esp_err_t err = i2s_channel_enable(rx);
    if (err != ESP_OK) {
        return err;
    }

    while (stats->count < sample_count && !rx_overflow) {
        const size_t remaining = warmup_left + sample_count - stats->count;
        const size_t frames = remaining < DMA_FRAMES ? remaining : DMA_FRAMES;
        size_t bytes_read = 0;
        err = i2s_channel_read(rx, input, frames * sizeof(input[0]), &bytes_read, 250);
        if (err != ESP_OK) {
            break;
        }
        if (bytes_read != frames * sizeof(input[0])) {
            err = ESP_ERR_INVALID_SIZE;
            break;
        }
        for (size_t i = 0; i < frames; i++) {
            if (warmup_left) {
                warmup_left--;
                continue;
            }
            // The signed shift discards the slot padding and the eight lowest audio bits.
            const int32_t sample = input[i].left >> 16;
            const unsigned magnitude = abs(sample);
            if (pcm) {
                pcm[stats->count] = sample;
            }
            stats->count++;
            stats->zeros += sample == 0;
            stats->rails += sample == INT16_MIN || sample == INT16_MAX;
            if (sample < stats->min) {
                stats->min = sample;
            }
            if (sample > stats->max) {
                stats->max = sample;
            }
            if (magnitude > stats->peak) {
                stats->peak = magnitude;
            }
            stats->sum_squares += sample * sample;
        }
    }

    const esp_err_t stop_err = i2s_channel_disable(rx);
    if (stop_err != ESP_OK) {
        ESP_LOGE(TAG, "I2S RX disable failed: %s", esp_err_to_name(stop_err));
        return stop_err;
    }
    if (rx_overflow) {
        ESP_LOGE(TAG, "I2S RX DMA queue overflow");
        return ESP_ERR_INVALID_RESPONSE;
    }
    return err;
}

static const char *signal_status(const mic_stats_t *stats, double rms)
{
    if (stats->rails == stats->count) {
        return "invalid";
    }
    if (stats->zeros == stats->count) {
        return "silent";
    }
    if (stats->min == stats->max) {
        return "constant";
    }
    return rms <= 1.0 ? "silent" : "ok";
}

static esp_err_t send_status(httpd_req_t *req, const char *status, const char *message)
{
    httpd_resp_set_status(req, status);
    httpd_resp_set_type(req, "text/plain");
    return httpd_resp_sendstr(req, message);
}

static void capture_worker(void *arg)
{
    httpd_req_t *req = arg;
    const bool record = req->user_ctx != NULL;
    const size_t sample_count = record ? TEST_SAMPLES : LEVEL_SAMPLES;
    const size_t pcm_bytes = sample_count * sizeof(int16_t);
    wav_header_t *wav = NULL;
    if (record) {
        wav = heap_caps_malloc(sizeof(*wav) + pcm_bytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT);
    }

    esp_err_t response;
    httpd_resp_set_hdr(req, "Cache-Control", "no-store");
    if (record && !wav) {
        ESP_LOGE(TAG, "Cannot allocate WAV buffer");
        response = send_status(req, "503 Service Unavailable", "WAV buffer unavailable");
    } else {
        mic_stats_t stats = { .min = INT16_MAX, .max = INT16_MIN };
        int16_t *pcm = wav ? (int16_t *)(wav + 1) : NULL;
        const esp_err_t err = capture_pcm(pcm, sample_count, &stats);
        const double rms = stats.count ? sqrt((double)stats.sum_squares / stats.count) : 0.0;
        const char *status = err == ESP_OK ? signal_status(&stats, rms) : "invalid";
        ESP_LOG_LEVEL(strcmp(status, "ok") == 0 ? ESP_LOG_INFO : ESP_LOG_WARN,
                      TAG, "Capture %s: %zu/%zu samples, RMS %.2f, peak %u, zeros %zu, rails %zu, I2S %s",
                      status, stats.count, sample_count, rms, stats.peak, stats.zeros, stats.rails,
                      esp_err_to_name(err));

        if (err != ESP_OK) {
            response = send_status(req, "503 Service Unavailable", "I2S capture failed");
        } else if (wav) {
            *wav = (wav_header_t) {
                .riff = "RIFF",
                .riff_size = sizeof(*wav) + pcm_bytes - 8,
                .wave_fmt = "WAVEfmt ",
                .fmt_size = 16,
                .format = 1,
                .channels = 1,
                .sample_rate = SAMPLE_RATE,
                .byte_rate = SAMPLE_RATE * sizeof(int16_t),
                .block_align = sizeof(int16_t),
                .bits_per_sample = 16,
                .data = "data",
                .data_size = pcm_bytes,
            };
            httpd_resp_set_type(req, "audio/wav");
            httpd_resp_set_hdr(req, "X-Mic-Signal", status);
            response = httpd_resp_send(req, (const char *)wav, sizeof(*wav) + pcm_bytes);
        } else {
            char json[224];
            snprintf(json, sizeof(json),
                     "{\"sampleRateHz\":%d,\"sampleCount\":%zu,\"rms\":%.2f,\"peak\":%u,"
                     "\"zeroSamples\":%zu,\"railSamples\":%zu,\"signal\":\"%s\"}",
                     SAMPLE_RATE, stats.count, rms, stats.peak, stats.zeros, stats.rails, status);
            httpd_resp_set_type(req, "application/json");
            response = httpd_resp_sendstr(req, json);
        }
    }

    free(wav);
    if (response != ESP_OK) {
        shutdown(httpd_req_to_sockfd(req), SHUT_RDWR);
    }
    httpd_req_async_handler_complete(req);
    xSemaphoreGive(capture_slot);
    vTaskDelete(NULL);
}

static esp_err_t start_capture(httpd_req_t *req)
{
    if (!capture_slot) {
        return send_status(req, "503 Service Unavailable", "Microphone unavailable");
    }
    if (xSemaphoreTake(capture_slot, 0) != pdTRUE) {
        return send_status(req, "409 Conflict", "Microphone capture already in progress");
    }

    httpd_req_t *async_req;
    if (httpd_req_async_handler_begin(req, &async_req) != ESP_OK) {
        xSemaphoreGive(capture_slot);
        return send_status(req, "503 Service Unavailable", "Microphone request unavailable");
    }
    if (xTaskCreate(capture_worker, "mic_capture", 5120, async_req, 4, NULL) != pdPASS) {
        if (send_status(async_req, "503 Service Unavailable", "Microphone worker unavailable") != ESP_OK) {
            shutdown(httpd_req_to_sockfd(async_req), SHUT_RDWR);
        }
        httpd_req_async_handler_complete(async_req);
        xSemaphoreGive(capture_slot);
    }
    return ESP_OK;
}

esp_err_t microphone_http_register(httpd_handle_t server)
{
    static bool record;
    const httpd_uri_t test = {
        .uri = "/mic/test", .method = HTTP_GET, .handler = start_capture, .user_ctx = &record,
    };
    const httpd_uri_t level = {
        .uri = "/mic/level", .method = HTTP_GET, .handler = start_capture,
    };
    const esp_err_t err = httpd_register_uri_handler(server, &test);
    if (err != ESP_OK) {
        return err;
    }
    return httpd_register_uri_handler(server, &level);
}
