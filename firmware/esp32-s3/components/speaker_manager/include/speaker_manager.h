#pragma once

#include "esp_http_server.h"
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

// Reader must fill the requested PCM16 mono frames or return an error.
typedef esp_err_t (*speaker_reader_t)(void *context, int16_t *samples, size_t frames);
typedef void (*speaker_started_t)(void *context);
esp_err_t speaker_stream(size_t frames, speaker_reader_t reader, speaker_started_t started, void *context);
void speaker_cancel(void);
bool speaker_available(void);
esp_err_t speaker_init(void);
esp_err_t speaker_http_register(httpd_handle_t server);

#ifdef __cplusplus
}
#endif
