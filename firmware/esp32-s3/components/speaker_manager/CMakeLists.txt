idf_component_register(SRCS "speaker_manager.cpp"
                       INCLUDE_DIRS "include"
                       PRIV_REQUIRES esp_driver_i2s esp_timer lwip
                       REQUIRES esp_http_server)
