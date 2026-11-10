#include <stdint.h>
#include <stdio.h>

#include "driver/gpio.h"
#include "driver/i2c.h"
#include "esp_err.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

enum {
    SDA_PIN = GPIO_NUM_1,
    SCL_PIN = GPIO_NUM_2,
    FIRST_ADDRESS = 0x08,
    LAST_ADDRESS = 0x77,
    PROBE_TIMEOUT_MS = 50,
};

static const char *TAG = "oled_i2c_diag";

static void scan_bus(unsigned clock_khz)
{
    const i2c_config_t config = {
        .mode = I2C_MODE_MASTER,
        .sda_io_num = SDA_PIN,
        .scl_io_num = SCL_PIN,
        .sda_pullup_en = GPIO_PULLUP_ENABLE,
        .scl_pullup_en = GPIO_PULLUP_ENABLE,
        .master.clk_speed = clock_khz * 1000,
    };
    ESP_ERROR_CHECK(i2c_param_config(I2C_NUM_0, &config));
    ESP_ERROR_CHECK(i2c_driver_install(I2C_NUM_0, I2C_MODE_MASTER, 0, 0, 0));

    vTaskDelay(pdMS_TO_TICKS(20));
    ESP_LOGI(TAG, "%u kHz idle: SDA=%d SCL=%d", clock_khz,
             gpio_get_level(SDA_PIN), gpio_get_level(SCL_PIN));
    ESP_ERROR_CHECK(gpio_dump_io_configuration(stdout, (1ULL << SDA_PIN) | (1ULL << SCL_PIN)));

    unsigned ack = 0, nack = 0, timeout = 0, other = 0;
    for (unsigned address = FIRST_ADDRESS; address <= LAST_ADDRESS; ++address) {
        _Alignas(uint32_t) static uint8_t buffer[I2C_LINK_RECOMMENDED_SIZE(1)];
        i2c_cmd_handle_t command = i2c_cmd_link_create_static(buffer, sizeof(buffer));
        ESP_ERROR_CHECK(command != NULL ? ESP_OK : ESP_ERR_NO_MEM);
        ESP_ERROR_CHECK(i2c_master_start(command));
        ESP_ERROR_CHECK(i2c_master_write_byte(command, address << 1, true));
        ESP_ERROR_CHECK(i2c_master_stop(command));
        esp_err_t result = i2c_master_cmd_begin(I2C_NUM_0, command, pdMS_TO_TICKS(PROBE_TIMEOUT_MS));
        i2c_cmd_link_delete_static(command);

        switch (result) {
        case ESP_OK:
            ++ack;
            ESP_LOGI(TAG, "%u kHz 0x%02X ACK", clock_khz, address);
            break;
        case ESP_FAIL:
            ++nack;
            ESP_LOGI(TAG, "%u kHz 0x%02X NACK", clock_khz, address);
            break;
        case ESP_ERR_TIMEOUT:
            ++timeout;
            ESP_LOGW(TAG, "%u kHz 0x%02X TIMEOUT", clock_khz, address);
            break;
        default:
            ++other;
            ESP_LOGE(TAG, "%u kHz 0x%02X ERROR %s", clock_khz, address,
                     esp_err_to_name(result));
            break;
        }
    }

    ESP_LOGI(TAG, "%u kHz summary: ACK=%u NACK=%u TIMEOUT=%u OTHER=%u",
             clock_khz, ack, nack, timeout, other);
    ESP_ERROR_CHECK(i2c_driver_delete(I2C_NUM_0));
}

void app_main(void)
{
    vTaskDelay(pdMS_TO_TICKS(2000));
    ESP_LOGI(TAG, "I2C0 SDA=GPIO%d SCL=GPIO%d, internal pull-ups enabled, timeout=%d ms",
             SDA_PIN, SCL_PIN, PROBE_TIMEOUT_MS);
    ESP_LOGI(TAG, "Scanning 7-bit addresses 0x%02X-0x%02X with address-only writes",
             FIRST_ADDRESS, LAST_ADDRESS);

    scan_bus(10);
    scan_bus(50);
    scan_bus(100);
    ESP_LOGI(TAG, "Scan complete");
}
