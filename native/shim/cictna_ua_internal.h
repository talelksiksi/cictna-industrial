/*
 * Internal helpers shared by the shim's .c files. Not part of the public boundary.
 */
#ifndef CICTNA_UA_INTERNAL_H
#define CICTNA_UA_INTERNAL_H

#include "cictna_ua.h"

#include <open62541/plugin/log.h>
#include <open62541/types.h>

/* Finds the open62541 type for a namespace-0 DataType id or DefaultBinary encoding id. */
const UA_DataType *cictna_find_type(uint32_t id);

/* Decodes `len` bytes as `type` into a freshly allocated value. The whole input must be
 * consumed. Returns NULL and sets *status on failure. Free with UA_delete(value, type). */
void *cictna_decode(const UA_DataType *type, const uint8_t *data, size_t len, UA_StatusCode *status);

/* Encodes a value; the buffer is allocated by open62541 (free with UA_free / cictna_ua_free). */
UA_StatusCode cictna_encode(const void *value, const UA_DataType *type, uint8_t **out, size_t *out_len);

#ifdef UA_ENABLE_ENCRYPTION
#include <open62541/plugin/certificategroup.h>
/* Initializes `group` as open62541's in-memory certificate store, filled from a binary
 * TrustListDataType (NULL or empty: no trusted certificates, so everything is rejected). */
UA_StatusCode cictna_memorystore(UA_CertificateGroup *group, const uint8_t *trust_list, size_t len,
                                 const UA_Logger *logger);
#endif

/* Duplicates a NUL-terminated string into a UA_String (empty when NULL). */
UA_String cictna_string(const char *s);

/* ---- log queue ----------------------------------------------------------------------------
 *
 * open62541 logs through a UA_Logger plugin. Ours formats each line (open62541 uses its own
 * format specifiers, hence UA_String_vformat rather than vsnprintf) and appends it to a bounded
 * FIFO that the managed side drains with *_poll_log. When the queue is full the oldest line is
 * dropped and counted, so a flood of log lines can never grow memory without bound.
 */
typedef struct {
    int32_t level;
    int32_t category;
    char *message;
} cictna_log_entry;

typedef struct {
    cictna_log_entry *items;
    size_t capacity;
    size_t head;            /* index of the oldest entry */
    size_t count;
    uint32_t dropped;       /* lines lost since the last poll */
    char *current;          /* message handed out by the last poll, freed at the next one */
    int32_t min_level;
} cictna_log_queue;

void cictna_logq_init(cictna_log_queue *q, size_t capacity, int32_t min_level);
void cictna_logq_clear(cictna_log_queue *q);

/* Allocates a UA_Logger that writes into q. The logger's clear() frees only the logger itself;
 * the queue outlives it because open62541 may still log while it is being torn down. */
UA_Logger *cictna_logq_logger(cictna_log_queue *q);

int32_t cictna_logq_pop(cictna_log_queue *q, int32_t *level, int32_t *category,
                        const char **message, uint32_t *dropped);

#endif /* CICTNA_UA_INTERNAL_H */
