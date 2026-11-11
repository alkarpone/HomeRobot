#include "robot_audio.h"
#include "speaker_manager.h"
#include "cJSON.h"
#include "esp_http_client.h"
#include "esp_websocket_client.h"
#include "esp_log.h"
#include "esp_random.h"
#include "esp_timer.h"
#include "esp_sntp.h"
#include <ctime>
#include <new>
#include "mbedtls/base64.h"
#include "nvs.h"
#include "freertos/FreeRTOS.h"
#include "freertos/queue.h"
#include "freertos/semphr.h"
#include "freertos/task.h"
#include <algorithm>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <cstring>

namespace {
constexpr char tag[] = "robot_audio";
esp_websocket_client_handle_t ws;
QueueHandle_t incoming, outgoing;
SemaphoreHandle_t gate;
std::atomic<bool> connected{false};
std::atomic<unsigned> generation{0};
char *base, *robot_id, *certificate;
char authorization[512];
struct Message { unsigned generation; char *text; };
struct Job {
    char command[65], playback[65], stop[65], resource[128], token[65];
    size_t bytes;
    unsigned generation;
    esp_http_client_handle_t http;
    int64_t deadline;
    std::atomic<bool> cancelled{false};
};
Job *active;

const char *string(cJSON *object, const char *key)
{
    auto *value = cJSON_GetObjectItemCaseSensitive(object, key);
    return cJSON_IsString(value) ? value->valuestring : "";
}
bool identifier(const char *value)
{
    const size_t length = strlen(value);
    return length > 0 && length <= 64 && strspn(value, "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_.-") == length;
}

void send(const char *type, cJSON *payload, unsigned epoch)
{
    auto *envelope = cJSON_CreateObject();
    char id[33];
    snprintf(id, sizeof(id), "%08lx%08lx%08lx%08lx", (unsigned long)esp_random(),
        (unsigned long)esp_random(), (unsigned long)esp_random(), (unsigned long)esp_random());
    cJSON_AddStringToObject(envelope, "id", id);
    cJSON_AddStringToObject(envelope, "type", type);
    char timestamp[32];
    time_t now = time(nullptr);
    struct tm utc{};
    gmtime_r(&now, &utc);
    strftime(timestamp, sizeof(timestamp), "%Y-%m-%dT%H:%M:%SZ", &utc);
    cJSON_AddStringToObject(envelope, "timestamp", timestamp);
    cJSON_AddItemToObject(envelope, "payload", payload);
    Message message{epoch, cJSON_PrintUnformatted(envelope)};
    cJSON_Delete(envelope);
    if (message.text && xQueueSend(outgoing, &message, 0) != pdTRUE) free(message.text);
}
void status(const char *command, const char *state, bool result, unsigned epoch, const char *error = nullptr)
{
    auto *payload = cJSON_CreateObject();
    cJSON_AddStringToObject(payload, "commandId", command);
    cJSON_AddStringToObject(payload, "status", state);
    if (result) {
        if (error) cJSON_AddStringToObject(payload, "message", error);
        else cJSON_AddNullToObject(payload, "message");
    }
    send(result ? "command_result" : "command_ack", payload, epoch);
}
void started(void *context)
{
    auto *job = static_cast<Job *>(context);
    ESP_LOGI(tag, "Playback started");
    status(job->command, "executing", false, job->generation);
}
esp_err_t read_pcm(void *context, int16_t *samples, size_t frames)
{
    auto *job = static_cast<Job *>(context);
    size_t received = 0;
    auto *bytes = reinterpret_cast<char *>(samples);
    while (received < frames * 2) {
        if (job->cancelled || !connected || generation != job->generation || esp_timer_get_time() > job->deadline)
            return ESP_ERR_TIMEOUT;
        int count = esp_http_client_read(job->http, bytes + received, frames * 2 - received);
        if (count <= 0) { ESP_LOGW(tag, "Audio network interruption or incomplete download"); return ESP_FAIL; }
        received += count;
    }
    return ESP_OK;
}
void playback(void *context)
{
    auto *job = static_cast<Job *>(context);
    char url[384];
    snprintf(url, sizeof(url), "%s%s", base, job->resource);
    esp_http_client_config_t config{};
    config.url = url;
    config.cert_pem = certificate;
    config.timeout_ms = 2000;
    config.disable_auto_redirect = true;
    config.buffer_size = 2048;
    job->http = esp_http_client_init(&config);
    job->deadline = esp_timer_get_time() + 40'000'000;
    esp_err_t err = ESP_FAIL;
    if (job->http) {
        esp_http_client_set_header(job->http, "Authorization", authorization);
        esp_http_client_set_header(job->http, "X-Audio-Token", job->token);
        ESP_LOGI(tag, "Audio download started");
        if (!job->cancelled && esp_http_client_open(job->http, 0) == ESP_OK &&
            esp_http_client_fetch_headers(job->http) == static_cast<int64_t>(job->bytes) &&
            esp_http_client_get_status_code(job->http) == 200 && !esp_http_client_is_chunked_response(job->http)) {
            ESP_LOGI(tag, "Audio buffering started");
            if (!job->cancelled) err = speaker_stream(job->bytes / 2, read_pcm, started, job);
        }
        esp_http_client_close(job->http);
        esp_http_client_cleanup(job->http);
    }
    xSemaphoreTake(gate, portMAX_DELAY);
    bool cancelled = job->cancelled;
    status(job->command, cancelled ? "cancelled" : err == ESP_OK ? "completed" : "failed", true,
        job->generation, cancelled || err == ESP_OK ? nullptr : "Audio download, format or I2S playback failed");
    if (job->stop[0]) status(job->stop, "completed", true, job->generation);
    active = nullptr;
    xSemaphoreGive(gate);
    ESP_LOGI(tag, "Playback %s", cancelled ? "stopped" : err == ESP_OK ? "completed" : "failed");
    delete job;
    vTaskDelete(nullptr);
}

void command(cJSON *envelope, unsigned epoch)
{
    const char *id = string(envelope, "id");
    if (!identifier(id)) return;
    auto *payload = cJSON_GetObjectItemCaseSensitive(envelope, "payload");
    auto *parameters = cJSON_GetObjectItemCaseSensitive(payload, "parameters");
    const char *name = string(payload, "command");
    const char *playback_id = string(parameters, "playbackId");
    xSemaphoreTake(gate, portMAX_DELAY);
    if (strcmp(name, "audio_stop") == 0) {
        if (active && active->generation == epoch && strcmp(active->playback, playback_id) == 0 && !active->stop[0]) {
            strcpy(active->stop, id);
            active->cancelled = true;
            speaker_cancel();
            status(id, "accepted", false, epoch);
        } else status(id, "failed", true, epoch, "Playback is not active");
    } else if (strcmp(name, "audio_play") == 0) {
        static char previous[64][65]{};
        static unsigned previous_count = 0;
        bool repeated = false;
        for (unsigned i = 0; i < std::min(previous_count, 64u); ++i)
            if (strcmp(previous[i], playback_id) == 0) repeated = true;
        char expected[128];
        snprintf(expected, sizeof(expected), "/api/audio/resources/%s", playback_id);
        const char *resource = string(parameters, "resource");
        const char *token = string(parameters, "token");
        auto number = [parameters](const char *key) { return cJSON_GetNumberValue(cJSON_GetObjectItemCaseSensitive(parameters, key)); };
        double bytes = number("byteLength");
        if (active || repeated || !identifier(playback_id) || strcmp(resource, expected) != 0 || strlen(token) != 64 ||
            strspn(token, "0123456789ABCDEF") != 64 || strcmp(string(parameters, "encoding"), "pcm_s16le") != 0 ||
            number("sampleRate") != 16000 || number("channels") != 1 || number("bitsPerSample") != 16 ||
            !(bytes > 0 && bytes <= 960000) || bytes != static_cast<int>(bytes) || static_cast<int>(bytes) % 2) {
            ESP_LOGW(tag, "Audio command rejected: busy or format mismatch");
            status(id, "failed", true, epoch, "Speaker busy or unsupported audio format");
        } else {
            auto *job = new (std::nothrow) Job{};
            if (job) {
                strcpy(job->command, id); strcpy(job->playback, playback_id);
                strcpy(job->resource, resource); strcpy(job->token, token);
                job->bytes = bytes; job->generation = epoch;
                active = job;
                strcpy(previous[previous_count++ % 64], playback_id);
                status(id, "accepted", false, epoch);
                ESP_LOGI(tag, "Playback command received");
                if (xTaskCreate(playback, "robot_pcm", 6144, job, 5, nullptr) != pdPASS) {
                    active = nullptr; delete job;
                    status(id, "failed", true, epoch, "Playback worker unavailable");
                }
            } else status(id, "failed", true, epoch, "Playback memory unavailable");
        }
    } else status(id, "failed", true, epoch, "Unsupported command");
    xSemaphoreGive(gate);
}

void event(void *, esp_event_base_t, int32_t id, void *data)
{
    static char buffer[4097];
    static size_t used;
    if (id == WEBSOCKET_EVENT_CONNECTED) {
        used = 0; generation++; connected = true;
        ESP_LOGI(tag, "Server connected");
    } else if (id == WEBSOCKET_EVENT_DISCONNECTED || id == WEBSOCKET_EVENT_ERROR || id == WEBSOCKET_EVENT_CLOSED) {
        connected = false;
        xSemaphoreTake(gate, portMAX_DELAY);
        if (active) { active->cancelled = true; speaker_cancel(); }
        xSemaphoreGive(gate);
        ESP_LOGW(tag, "Server connection interrupted");
    } else if (id == WEBSOCKET_EVENT_DATA) {
        auto *frame = static_cast<esp_websocket_event_data_t *>(data);
        if (frame->op_code != 1 && frame->op_code != 0) return;
        if (frame->op_code == 1 && frame->payload_offset == 0) used = 0;
        if (frame->data_len < 0 || used + frame->data_len > sizeof(buffer) - 1) { used = sizeof(buffer); return; }
        memcpy(buffer + used, frame->data_ptr, frame->data_len); used += frame->data_len;
        if (frame->fin && frame->payload_offset + frame->data_len == frame->payload_len) {
            buffer[used] = 0;
            Message message{generation, strdup(buffer)};
            if (message.text && xQueueSend(incoming, &message, 0) != pdTRUE) free(message.text);
            used = 0;
        }
    }
}

void worker(void *)
{
    unsigned epoch = 0;
    bool registered = false;
    int heartbeat_seconds = 5;
    int64_t last_heartbeat = 0;
    static char recent[256][65]{};
    unsigned recent_count = 0;
    for (;;) {
        if (connected && epoch != generation) {
            epoch = generation; registered = false; recent_count = 0;
            auto *registration = cJSON_CreateObject();
            cJSON_AddStringToObject(registration, "robotId", robot_id);
            cJSON_AddStringToObject(registration, "name", "HomeRobot ESP32 Speaker");
            cJSON_AddStringToObject(registration, "deviceType", "esp32");
            cJSON_AddNumberToObject(registration, "protocolVersion", 1);
            const char *capabilities[] = {"audio_output", "tts_playback", "audio_stop"};
            cJSON_AddItemToObject(registration, "capabilities", cJSON_CreateStringArray(capabilities, 3));
            send("register", registration, epoch);
        }
        Message message;
        if (xQueueReceive(incoming, &message, pdMS_TO_TICKS(100)) == pdTRUE) {
            auto *root = cJSON_Parse(message.text); free(message.text);
            if (root && connected && message.generation == epoch) {
                const char *type = string(root, "type");
                if (strcmp(type, "register_ack") == 0) {
                    registered = true; last_heartbeat = 0;
                    auto *payload = cJSON_GetObjectItemCaseSensitive(root, "payload");
                    double interval = cJSON_GetNumberValue(cJSON_GetObjectItemCaseSensitive(payload, "heartbeatIntervalSeconds"));
                    if (interval >= 1 && interval <= 60) heartbeat_seconds = interval;
                } else if (registered && strcmp(type, "command") == 0) {
                    const char *id = string(root, "id");
                    bool duplicate = false;
                    for (unsigned i = 0; i < std::min(recent_count, 256u); ++i)
                        if (strcmp(recent[i], id) == 0) duplicate = true;
                    if (!duplicate && identifier(id)) {
                        strcpy(recent[recent_count++ % 256], id);
                        command(root, epoch);
                    }
                }
            }
            cJSON_Delete(root);
        }
        if (connected && registered && esp_timer_get_time() - last_heartbeat >= heartbeat_seconds * 1'000'000LL) {
            auto *payload = cJSON_CreateObject(); cJSON_AddStringToObject(payload, "robotId", robot_id);
            send("heartbeat", payload, epoch); last_heartbeat = esp_timer_get_time();
        }
        while (xQueueReceive(outgoing, &message, 0) == pdTRUE) {
            if (connected && message.generation == epoch)
                esp_websocket_client_send_text(ws, message.text, strlen(message.text), pdMS_TO_TICKS(1000));
            free(message.text);
        }
    }
}

char *setting(nvs_handle_t nvs, const char *key)
{
    size_t length = 0;
    if (nvs_get_str(nvs, key, nullptr, &length) != ESP_OK || length > 8192) return nullptr;
    auto *value = static_cast<char *>(malloc(length));
    if (value && nvs_get_str(nvs, key, value, &length) != ESP_OK) { free(value); return nullptr; }
    return value;
}
} // namespace

void robot_audio_start(void)
{
    nvs_handle_t nvs;
    if (!speaker_available() || nvs_open_from_partition("wifi_cfg", "homerobot", NVS_READONLY, &nvs) != ESP_OK) return;
    base = setting(nvs, "server_url"); robot_id = setting(nvs, "robot_id");
    auto *password = setting(nvs, "server_password"); certificate = setting(nvs, "server_ca");
    nvs_close(nvs);
    // The existing server LAN boundary requires verified HTTPS and admin Basic authentication.
    if (!base || !robot_id || !password || !certificate || strncmp(base, "https://", 8) != 0 ||
        strlen(base) > 240 || strchr(base + 8, '/') || strchr(base, '?') || strchr(base, '#') || strchr(base, '@') ||
        !identifier(robot_id) || strlen(password) < 24 || strlen(password) > 128) {
        ESP_LOGW(tag, "Server audio disabled: provision HTTPS server settings");
        free(base); free(robot_id); free(password); free(certificate); return;
    }
    char credentials[140]; snprintf(credentials, sizeof(credentials), "admin:%s", password);
    size_t encoded = 0;
    strcpy(authorization, "Basic ");
    mbedtls_base64_encode(reinterpret_cast<unsigned char *>(authorization + 6), sizeof(authorization) - 6,
        &encoded, reinterpret_cast<unsigned char *>(credentials), strlen(credentials));
    memset(credentials, 0, sizeof(credentials)); memset(password, 0, strlen(password)); free(password);
    // Certificate validation requires current time; reconnect retries while SNTP synchronizes.
    esp_sntp_setoperatingmode(SNTP_OPMODE_POLL);
    esp_sntp_setservername(0, "pool.ntp.org");
    esp_sntp_init();
    incoming = xQueueCreate(4, sizeof(Message)); outgoing = xQueueCreate(16, sizeof(Message));
    gate = xSemaphoreCreateMutex();
    if (!incoming || !outgoing || !gate) { ESP_LOGE(tag, "Audio queues unavailable"); return; }
    char url[280]; snprintf(url, sizeof(url), "wss%s/ws/robot", base + 5);
    char headers[560]; snprintf(headers, sizeof(headers), "Authorization: %s\r\n", authorization);
    esp_websocket_client_config_t config{};
    config.uri = url; config.headers = headers; config.cert_pem = certificate;
    config.reconnect_timeout_ms = 3000; config.network_timeout_ms = 2000;
    config.enable_close_reconnect = true; config.buffer_size = 4096;
    ws = esp_websocket_client_init(&config);
    if (!ws) return;
    esp_websocket_register_events(ws, WEBSOCKET_EVENT_ANY, event, nullptr);
    if (xTaskCreate(worker, "robot_control", 6144, nullptr, 4, nullptr) != pdPASS) return;
    esp_websocket_client_start(ws);
}
