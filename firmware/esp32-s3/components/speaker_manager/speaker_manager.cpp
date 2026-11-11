#include "speaker_manager.h"

#include <algorithm>
#include <atomic>
#include <cmath>
#include <cinttypes>
#include <cstring>
#include <cstdint>
#include <cstdlib>
#include <strings.h>

#include "driver/i2s_std.h"
#include "esp_heap_caps.h"
#include "esp_log.h"
#include "esp_timer.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "lwip/sockets.h"

namespace {

constexpr char tag[] = "speaker";
constexpr auto bclk = GPIO_NUM_41;
constexpr auto lrclk = GPIO_NUM_42;
constexpr auto dout = GPIO_NUM_14;
constexpr int sample_rate = 16000;
constexpr int tone_hz = 440;
constexpr int tone_peak = 8192;
constexpr unsigned default_volume = 20;
constexpr size_t max_pcm_bytes = 30 * sample_rate * sizeof(int16_t);
constexpr size_t frames_per_buffer = 160;
constexpr size_t dma_buffers = 4;
constexpr int64_t upload_timeout_us = 8'000'000;

i2s_chan_handle_t tx;
std::atomic<bool> busy{false};
std::atomic<bool> cancelled{false};
std::atomic<bool> streaming{false};
volatile uint32_t underruns; // Written by the I2S ISR. Read and reset with TX disabled.

bool IRAM_ATTR on_send_queue_overflow(i2s_chan_handle_t, i2s_event_data_t *, void *)
{
    underruns = underruns + 1;
    return false;
}

esp_err_t write_frames(const int16_t (&stereo)[frames_per_buffer * 2])
{
    size_t written = 0;
    const esp_err_t err = i2s_channel_write(tx, stereo, sizeof(stereo), &written, 200);
    if (err == ESP_OK && written == sizeof(stereo)) {
        return ESP_OK;
    }
    ESP_LOGE(tag, "I2S write failed (%s, %zu/%zu bytes)",
             esp_err_to_name(err), written, sizeof(stereo));
    return err == ESP_OK ? ESP_FAIL : err;
}

esp_err_t play(const int16_t *pcm, size_t frames, unsigned volume,
               speaker_reader_t reader = nullptr, speaker_started_t started = nullptr, void *context = nullptr)
{
    int16_t stereo[frames_per_buffer * 2]{};
    // Start with 40 ms of silence and clear stale DMA data from any failed job.
    for (size_t i = 0; i < dma_buffers; ++i) {
        size_t loaded = 0;
        const esp_err_t err = i2s_channel_preload_data(tx, stereo, sizeof(stereo), &loaded);
        if (err != ESP_OK || loaded != sizeof(stereo)) {
            return err == ESP_OK ? ESP_FAIL : err;
        }
    }
    ESP_LOGI(tag, "Playback start: %s, %zu frames, volume %u%%",
             reader ? "streamed PCM" : pcm ? "PCM" : "440 Hz test tone", frames, volume);
    underruns = 0;
    esp_err_t err = i2s_channel_enable(tx);
    if (err != ESP_OK) {
        return err;
    }

    int16_t chunk[frames_per_buffer];
    for (size_t offset = 0; offset < frames && err == ESP_OK; offset += frames_per_buffer) {
        if (cancelled.load()) { err = ESP_ERR_INVALID_STATE; break; }
        const size_t count = std::min(frames_per_buffer, frames - offset);
        if (reader) {
            err = reader(context, chunk, count);
            if (err != ESP_OK || cancelled.load()) { err = ESP_FAIL; break; }
            if (offset == 0 && started) started(context);
        }
        for (size_t i = 0; i < count; ++i) {
            const size_t frame = offset + i;
            const int sample = reader ? chunk[i] : pcm ? pcm[frame] :
                static_cast<int>(tone_peak * std::sin(2.0f * 3.14159265f *
                    ((frame * tone_hz) % sample_rate) / sample_rate));
            stereo[i * 2] = stereo[i * 2 + 1] = sample * static_cast<int>(volume) / 100;
        }
        std::fill(stereo + count * 2, stereo + frames_per_buffer * 2, 0);
        err = write_frames(stereo);
    }

    // Wait for the last audio buffer to go around the DMA ring before stopping clocks.
    std::fill_n(stereo, frames_per_buffer * 2, 0);
    for (size_t i = 0; i < dma_buffers && err == ESP_OK; ++i) {
        err = write_frames(stereo);
    }
    const esp_err_t stop_err = i2s_channel_disable(tx);
    if (underruns != 0) {
        ESP_LOGW(tag, "I2S buffer underrun: %" PRIu32 " TX queue overruns", underruns);
    }
    return err == ESP_OK ? stop_err : err;
}

struct PlaybackRequest {
    httpd_req_t *http;
    unsigned volume;
};

esp_err_t reply(httpd_req_t *req, const char *status, const char *message, bool close = false)
{
    httpd_resp_set_status(req, status);
    httpd_resp_set_type(req, "text/plain");
    if (close) {
        httpd_resp_set_hdr(req, "Connection", "close");
    }
    const esp_err_t err = httpd_resp_sendstr(req, message);
    return close ? ESP_FAIL : err;
}

bool receive_pcm(httpd_req_t *req, int16_t *pcm)
{
    const int64_t deadline = esp_timer_get_time() + upload_timeout_us;
    auto *bytes = reinterpret_cast<char *>(pcm);
    size_t received = 0;
    while (received < req->content_len && esp_timer_get_time() < deadline) {
        const int result = httpd_req_recv(req, bytes + received, req->content_len - received);
        if (result <= 0) {
            break;
        }
        received += result;
    }
    return received == req->content_len && esp_timer_get_time() <= deadline;
}

void playback_task(void *arg)
{
    auto *job = static_cast<PlaybackRequest *>(arg);
    auto *req = job->http;
    const unsigned volume = job->volume;
    free(job);

    const size_t bytes = req->content_len;
    int16_t *pcm = nullptr;
    esp_err_t response;
    if (bytes != 0) {
        pcm = static_cast<int16_t *>(heap_caps_malloc(bytes, MALLOC_CAP_SPIRAM | MALLOC_CAP_8BIT));
    }
    if (bytes != 0 && !pcm) {
        ESP_LOGE(tag, "Cannot allocate %zu bytes for PCM upload", bytes);
        response = reply(req, "503 Service Unavailable", "Audio buffer unavailable", true);
    } else if (pcm && !receive_pcm(req, pcm)) {
        ESP_LOGW(tag, "PCM upload incomplete or timed out");
        response = reply(req, "408 Request Timeout", "PCM upload incomplete or timed out", true);
    } else {
        response = reply(req, "202 Accepted", "Playback accepted");
    }
    if (response != ESP_OK) {
        shutdown(httpd_req_to_sockfd(req), SHUT_RDWR);
    }
    httpd_req_async_handler_complete(req);

    if (response == ESP_OK) {
        const esp_err_t err = play(pcm, pcm ? bytes / sizeof(int16_t) : sample_rate, volume);
        if (err == ESP_OK) {
            ESP_LOGI(tag, "Playback complete");
        } else {
            ESP_LOGE(tag, "Playback failed: %s", esp_err_to_name(err));
        }
    }
    free(pcm);
    busy.store(false);
    vTaskDelete(nullptr);
}

esp_err_t submit(httpd_req_t *req)
{
    const bool test = req->user_ctx == nullptr;
    const size_t bytes = req->content_len;
    if (httpd_req_get_hdr_value_len(req, "Transfer-Encoding") != 0) {
        return reply(req, "400 Bad Request", "Send a fixed-length body", true);
    }
    if (test && bytes != 0) {
        return reply(req, "400 Bad Request", "Test tone requires an empty body", true);
    }
    if (!test) {
        if (bytes > max_pcm_bytes) {
            return reply(req, "413 Payload Too Large", "PCM exceeds 30 seconds", true);
        }
        if (bytes == 0 || bytes % sizeof(int16_t) != 0) {
            return reply(req, "400 Bad Request", "PCM requires complete 16-bit samples", true);
        }
        char type[32];
        if (httpd_req_get_hdr_value_str(req, "Content-Type", type, sizeof(type)) != ESP_OK ||
            strcasecmp(type, "application/octet-stream") != 0) {
            return reply(req, "415 Unsupported Media Type", "Expected application/octet-stream", true);
        }
    }

    unsigned volume = default_volume;
    if (httpd_req_get_hdr_value_len(req, "X-Audio-Volume") != 0) {
        char value[4];
        if (httpd_req_get_hdr_value_str(req, "X-Audio-Volume", value, sizeof(value)) != ESP_OK ||
            value[strspn(value, "0123456789")] != '\0' || (volume = atoi(value)) > 100) {
            return reply(req, "400 Bad Request", "X-Audio-Volume must be an integer from 0 to 100", bytes != 0);
        }
    }
    if (!tx) {
        return reply(req, "503 Service Unavailable", "Speaker unavailable", bytes != 0);
    }
    if (busy.exchange(true)) {
        return reply(req, "409 Conflict", "Speaker is busy", bytes != 0);
    }

    cancelled.store(false);
    auto *job = static_cast<PlaybackRequest *>(malloc(sizeof(PlaybackRequest)));
    if (job && httpd_req_async_handler_begin(req, &job->http) == ESP_OK) {
        job->volume = volume;
        if (xTaskCreate(playback_task, "speaker", 4096, job, 5, nullptr) == pdPASS) {
            return ESP_OK;
        }
        httpd_req_async_handler_complete(job->http);
    }
    free(job);
    busy.store(false);
    ESP_LOGE(tag, "Cannot start playback worker");
    return reply(req, "503 Service Unavailable", "Cannot start playback", bytes != 0);
}

} // namespace

bool speaker_available(void) { return tx != nullptr; }

void speaker_cancel(void) { if (streaming.load()) cancelled.store(true); }

esp_err_t speaker_stream(size_t frames, speaker_reader_t reader, speaker_started_t started, void *context)
{
    if (!tx || !reader || frames == 0 || frames > max_pcm_bytes / sizeof(int16_t)) return ESP_ERR_INVALID_ARG;
    if (busy.exchange(true)) return ESP_ERR_INVALID_STATE;
    cancelled.store(false);
    streaming.store(true);
    const esp_err_t err = play(nullptr, frames, default_volume, reader, started, context);
    streaming.store(false);
    busy.store(false);
    return err;
}

esp_err_t speaker_init(void)
{
    i2s_chan_config_t channel = I2S_CHANNEL_DEFAULT_CONFIG(I2S_NUM_AUTO, I2S_ROLE_MASTER);
    channel.dma_desc_num = dma_buffers;
    channel.dma_frame_num = frames_per_buffer;
    channel.auto_clear_after_cb = true;
    esp_err_t err = i2s_new_channel(&channel, &tx, nullptr);
    if (err != ESP_OK) {
        return err;
    }
    i2s_std_config_t config{};
    config.clk_cfg = I2S_STD_CLK_DEFAULT_CONFIG(sample_rate);
    config.slot_cfg = I2S_STD_PHILIPS_SLOT_DEFAULT_CONFIG(I2S_DATA_BIT_WIDTH_16BIT, I2S_SLOT_MODE_STEREO);
    config.gpio_cfg.mclk = I2S_GPIO_UNUSED;
    config.gpio_cfg.bclk = bclk;
    config.gpio_cfg.ws = lrclk;
    config.gpio_cfg.dout = dout;
    config.gpio_cfg.din = I2S_GPIO_UNUSED;
    err = i2s_channel_init_std_mode(tx, &config);
    if (err == ESP_OK) {
        i2s_event_callbacks_t callbacks{};
        callbacks.on_send_q_ovf = on_send_queue_overflow;
        err = i2s_channel_register_event_callback(tx, &callbacks, nullptr);
    }
    if (err != ESP_OK) {
        i2s_del_channel(tx);
        tx = nullptr;
        return err;
    }
    ESP_LOGI(tag, "I2S TX ready: Philips PCM16 stereo, %d Hz, BCLK GPIO%d, LRCLK GPIO%d, DOUT GPIO%d",
             sample_rate, bclk, lrclk, dout);
    return ESP_OK;
}

esp_err_t speaker_http_register(httpd_handle_t server)
{
    static bool play_route;
    httpd_uri_t route{};
    route.method = HTTP_POST;
    route.handler = submit;
    route.uri = "/audio/test";
    const esp_err_t err = httpd_register_uri_handler(server, &route);
    if (err != ESP_OK) {
        return err;
    }
    route.uri = "/audio/play";
    route.user_ctx = &play_route;
    return httpd_register_uri_handler(server, &route);
}
