#pragma once

void oled_show_startup_face(void);

#include "esp_http_server.h"
esp_err_t oled_http_register(httpd_handle_t server);
