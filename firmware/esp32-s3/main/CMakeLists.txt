idf_component_register(SRCS "main.c" "camera_http.c" "wifi.c" "oled.c" "microphone.c" "robot_audio.cpp"
                       INCLUDE_DIRS "."
                       PRIV_REQUIRES esp_http_server esp_wifi esp_netif esp_event
                                     esp_timer nvs_flash spi_flash esp_psram
                                     esp_app_format esp_driver_i2c esp_driver_i2s esp_driver_gpio json lwip speaker_manager esp_http_client esp_websocket_client mbedtls)
