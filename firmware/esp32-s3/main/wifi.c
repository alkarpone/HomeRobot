#include "wifi.h"

#include "esp_event.h"
#include "esp_log.h"
#include "esp_netif.h"
#include "esp_timer.h"
#include "esp_wifi.h"
#include "nvs_flash.h"

static const char *TAG = "wifi";
static esp_timer_handle_t retry_timer;

static esp_err_t load_credentials(wifi_sta_config_t *config)
{
    esp_err_t err = nvs_flash_init_partition("wifi_cfg");
    if (err != ESP_OK) {
        return err;
    }

    nvs_handle_t nvs;
    err = nvs_open_from_partition("wifi_cfg", "homerobot", NVS_READONLY, &nvs);
    if (err != ESP_OK) {
        return err;
    }
    size_t ssid_length = sizeof(config->ssid);
    size_t password_length = sizeof(config->password) - 1;
    err = nvs_get_blob(nvs, "ssid", config->ssid, &ssid_length);
    if (err == ESP_OK) {
        err = nvs_get_blob(nvs, "password", config->password, &password_length);
    }
    nvs_close(nvs);

    if (err == ESP_OK && (ssid_length == 0 || password_length < 8)) {
        return ESP_ERR_INVALID_ARG;
    }
    return err;
}

static void schedule_retry(void)
{
    esp_err_t err = esp_timer_start_once(retry_timer, 3000000);
    if (err != ESP_OK && err != ESP_ERR_INVALID_STATE) {
        ESP_LOGW(TAG, "Cannot schedule retry: %s", esp_err_to_name(err));
    }
}

static void connect(void *arg)
{
    esp_err_t err = esp_wifi_connect();
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "Cannot start connection: %s", esp_err_to_name(err));
        schedule_retry();
    }
}

static void wifi_event(void *arg, esp_event_base_t base, int32_t id, void *data)
{
    if (base == WIFI_EVENT && id == WIFI_EVENT_STA_START) {
        connect(NULL);
    } else if (base == WIFI_EVENT && id == WIFI_EVENT_STA_DISCONNECTED) {
        const wifi_event_sta_disconnected_t *event = data;
        ESP_LOGW(TAG, "Disconnected (reason %u), retrying in 3 seconds", event->reason);
        schedule_retry();
    } else if (base == IP_EVENT && id == IP_EVENT_STA_GOT_IP) {
        const ip_event_got_ip_t *event = data;
        ESP_LOGI(TAG, "Camera HTTP server: http://" IPSTR, IP2STR(&event->ip_info.ip));
    }
}

esp_err_t wifi_start(void)
{
    wifi_config_t wifi = {
        .sta.threshold.authmode = WIFI_AUTH_WPA2_PSK,
        .sta.sae_pwe_h2e = WPA3_SAE_PWE_BOTH,
    };
    esp_err_t err = load_credentials(&wifi.sta);
    if (err != ESP_OK) {
        ESP_LOGW(TAG, "Provision Wi-Fi using .env and provision_wifi.py");
        return err;
    }

    ESP_ERROR_CHECK(nvs_flash_init());
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    if (!esp_netif_create_default_wifi_sta()) {
        return ESP_ERR_NO_MEM;
    }
    const esp_timer_create_args_t timer = { .callback = connect, .name = "wifi_retry" };
    ESP_ERROR_CHECK(esp_timer_create(&timer, &retry_timer));
    ESP_ERROR_CHECK(esp_event_handler_register(WIFI_EVENT, ESP_EVENT_ANY_ID, wifi_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_STA_GOT_IP, wifi_event, NULL));

    wifi_init_config_t init = WIFI_INIT_CONFIG_DEFAULT();
    init.nvs_enable = 0;
    ESP_ERROR_CHECK(esp_wifi_init(&init));
    ESP_ERROR_CHECK(esp_wifi_set_storage(WIFI_STORAGE_RAM));
    ESP_ERROR_CHECK(esp_wifi_set_mode(WIFI_MODE_STA));
    err = esp_wifi_set_config(WIFI_IF_STA, &wifi);
    if (err != ESP_OK) {
        return err;
    }
    return esp_wifi_start();
}
