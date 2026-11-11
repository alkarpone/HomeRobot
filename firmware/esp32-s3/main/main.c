#include "camera_http.h"
#include "esp_log.h"
#include "microphone.h"
#include "oled.h"
#include "speaker_manager.h"
#include "wifi.h"
#include "robot_audio.h"

void app_main(void)
{
    esp_err_t err = speaker_init();
    if (err != ESP_OK) {
        ESP_LOGE("main", "Speaker I2S initialization failed: %s", esp_err_to_name(err));
    }

    err = microphone_init();
    if (err != ESP_OK) {
        ESP_LOGE("main", "Microphone I2S initialization failed: %s", esp_err_to_name(err));
    }

    err = camera_init();
    if (err != ESP_OK) {
        ESP_LOGE("main", "Camera initialization failed: %s", esp_err_to_name(err));
    }

    oled_show_startup_face();

    err = wifi_start();
    if (err != ESP_OK) {
        ESP_LOGE("main", "Wi-Fi startup failed: %s", esp_err_to_name(err));
        return;
    }
    ESP_ERROR_CHECK(camera_http_start());
    robot_audio_start();
}
