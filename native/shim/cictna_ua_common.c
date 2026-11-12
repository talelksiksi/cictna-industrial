/*
 * cictna_ua_common.c: helpers shared by client and server, plus the codec, memory and
 * certificate functions of the public API.
 */
#include "cictna_ua_internal.h"

#include <open62541/plugin/create_certificate.h>

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- types ----------------------------------------------------------------------------- */

const UA_DataType *cictna_find_type(uint32_t id) {
    /* Linear scan over the ~400 namespace-0 types. Called once per request, which is cheap
     * compared to the network round trip; a table would need rebuilding on version bumps. */
    for(size_t i = 0; i < UA_TYPES_COUNT; i++) {
        const UA_DataType *t = &UA_TYPES[i];
        if(t->typeId.namespaceIndex == 0 && t->typeId.identifierType == UA_NODEIDTYPE_NUMERIC &&
           t->typeId.identifier.numeric == id)
            return t;
        if(t->binaryEncodingId.namespaceIndex == 0 &&
           t->binaryEncodingId.identifierType == UA_NODEIDTYPE_NUMERIC &&
           t->binaryEncodingId.identifier.numeric == id)
            return t;
    }
    return NULL;
}

void *cictna_decode(const UA_DataType *type, const uint8_t *data, size_t len, UA_StatusCode *status) {
    void *value = UA_new(type);
    if(!value) {
        *status = UA_STATUSCODE_BADOUTOFMEMORY;
        return NULL;
    }
    UA_ByteString in = {len, (UA_Byte *)(uintptr_t)data};
    UA_DecodeBinaryOptions options;
    memset(&options, 0, sizeof(options));
    *status = UA_decodeBinary(&in, value, type, &options);
    if(*status == UA_STATUSCODE_GOOD && options.decodedLength != len)
        *status = UA_STATUSCODE_BADDECODINGERROR; /* trailing bytes: reject, never guess */
    if(*status != UA_STATUSCODE_GOOD) {
        UA_delete(value, type);
        return NULL;
    }
    return value;
}

UA_StatusCode cictna_encode(const void *value, const UA_DataType *type, uint8_t **out, size_t *out_len) {
    UA_ByteString buf = UA_BYTESTRING_NULL; /* length 0: open62541 allocates the right size */
    UA_StatusCode rc = UA_encodeBinary(value, type, &buf, NULL);
    if(rc != UA_STATUSCODE_GOOD) {
        UA_ByteString_clear(&buf);
        *out = NULL;
        *out_len = 0;
        return rc;
    }
    *out = buf.data;
    *out_len = buf.length;
    return UA_STATUSCODE_GOOD;
}

UA_String cictna_string(const char *s) {
    return s ? UA_STRING_ALLOC(s) : UA_STRING_NULL;
}

/* ---- public: memory and codec ------------------------------------------------------- */

void cictna_ua_free(void *buffer) {
    UA_free(buffer);
}

uint32_t cictna_ua_codec_roundtrip(uint32_t type_id, const uint8_t *in, size_t in_len,
                                   uint8_t **out, size_t *out_len, size_t *consumed) {
    *out = NULL;
    *out_len = 0;
    *consumed = 0;
    const UA_DataType *type = cictna_find_type(type_id);
    if(!type)
        return UA_STATUSCODE_BADDATATYPEIDUNKNOWN;

    /* Unlike cictna_decode, trailing bytes are allowed here and reported through *consumed,
     * so the tests can tell "decoded less than given" apart from "failed to decode". */
    void *value = UA_new(type);
    if(!value)
        return UA_STATUSCODE_BADOUTOFMEMORY;
    UA_ByteString buf = {in_len, (UA_Byte *)(uintptr_t)in};
    UA_DecodeBinaryOptions options;
    memset(&options, 0, sizeof(options));
    UA_StatusCode rc = UA_decodeBinary(&buf, value, type, &options);
    if(rc == UA_STATUSCODE_GOOD) {
        *consumed = options.decodedLength;
        rc = cictna_encode(value, type, out, out_len);
    }
    UA_delete(value, type);
    return rc;
}

/* ---- public: certificates ------------------------------------------------------------ */

uint32_t cictna_ua_create_certificate(const char *common_name, const char *organization,
                                      const char *application_uri, const char *dns_name,
                                      uint32_t key_size_bits, uint32_t valid_days,
                                      uint8_t **cert, size_t *cert_len,
                                      uint8_t **key, size_t *key_len) {
#ifdef UA_ENABLE_ENCRYPTION
    *cert = NULL; *cert_len = 0;
    *key = NULL; *key_len = 0;
    if(!common_name || !application_uri)
        return UA_STATUSCODE_BADINVALIDARGUMENT;

    /* Subject and subject alternative names in open62541's "KEY=value" element form. */
    char cn[512], o[512], uri[1024], dns[512];
    UA_String subject[2];
    size_t subject_size = 0;
    snprintf(cn, sizeof(cn), "CN=%s", common_name);
    subject[subject_size++] = UA_STRING(cn);
    if(organization && *organization) {
        snprintf(o, sizeof(o), "O=%s", organization);
        subject[subject_size++] = UA_STRING(o);
    }
    UA_String san[2];
    size_t san_size = 0;
    snprintf(uri, sizeof(uri), "URI:%s", application_uri);
    san[san_size++] = UA_STRING(uri);
    if(dns_name && *dns_name) {
        snprintf(dns, sizeof(dns), "DNS:%s", dns_name);
        san[san_size++] = UA_STRING(dns);
    }

    UA_UInt16 bits = (UA_UInt16)(key_size_bits ? key_size_bits : 2048);
    UA_UInt16 days = (UA_UInt16)(valid_days ? (valid_days > 65535 ? 65535 : valid_days) : 365);
    UA_KeyValueMap params = UA_KEYVALUEMAP_NULL;
    UA_KeyValueMap_setScalar(&params, UA_QUALIFIEDNAME(0, "key-size-bits"), &bits, &UA_TYPES[UA_TYPES_UINT16]);
    UA_KeyValueMap_setScalar(&params, UA_QUALIFIEDNAME(0, "expires-in-days"), &days, &UA_TYPES[UA_TYPES_UINT16]);

    /* open62541 requires a logger here. A short-lived queue collects anything it logs; the
     * status code is what reaches the caller. */
    cictna_log_queue log;
    cictna_logq_init(&log, 16, CICTNA_UA_LOG_ERROR);
    UA_Logger *logger = cictna_logq_logger(&log);
    UA_ByteString out_key = UA_BYTESTRING_NULL;
    UA_ByteString out_cert = UA_BYTESTRING_NULL;
    UA_StatusCode rc = logger
        ? UA_CreateCertificate(logger, subject, subject_size, san, san_size,
                               UA_CERTIFICATEFORMAT_DER, &params, &out_key, &out_cert)
        : UA_STATUSCODE_BADOUTOFMEMORY;
    if(logger)
        logger->clear(logger);
    cictna_logq_clear(&log);
    UA_KeyValueMap_clear(&params);
    if(rc != UA_STATUSCODE_GOOD) {
        UA_ByteString_clear(&out_key);
        UA_ByteString_clear(&out_cert);
        return rc;
    }
    *cert = out_cert.data; *cert_len = out_cert.length;
    *key = out_key.data; *key_len = out_key.length;
    return UA_STATUSCODE_GOOD;
#else
    (void)common_name; (void)organization; (void)application_uri; (void)dns_name;
    (void)key_size_bits; (void)valid_days; (void)cert; (void)cert_len; (void)key; (void)key_len;
    return UA_STATUSCODE_BADNOTSUPPORTED;
#endif
}

/* ---- log queue ------------------------------------------------------------------------- */

void cictna_logq_init(cictna_log_queue *q, size_t capacity, int32_t min_level) {
    memset(q, 0, sizeof(*q));
    q->items = (cictna_log_entry *)calloc(capacity, sizeof(cictna_log_entry));
    q->capacity = q->items ? capacity : 0;
    q->min_level = min_level ? min_level : CICTNA_UA_LOG_INFO;
}

void cictna_logq_clear(cictna_log_queue *q) {
    for(size_t i = 0; i < q->count; i++)
        free(q->items[(q->head + i) % q->capacity].message);
    free(q->items);
    free(q->current);
    memset(q, 0, sizeof(*q));
}

static void logq_push(cictna_log_queue *q, int32_t level, int32_t category, char *message) {
    if(q->capacity == 0) {
        free(message);
        q->dropped++;
        return;
    }
    if(q->count == q->capacity) {
        /* Full: drop the oldest line so the newest (usually most relevant) survive. */
        free(q->items[q->head].message);
        q->head = (q->head + 1) % q->capacity;
        q->count--;
        q->dropped++;
    }
    cictna_log_entry *e = &q->items[(q->head + q->count) % q->capacity];
    e->level = level;
    e->category = category;
    e->message = message;
    q->count++;
}

static void logq_log(void *context, UA_LogLevel level, UA_LogCategory category,
                     const char *msg, va_list args) {
    cictna_log_queue *q = (cictna_log_queue *)context;
    if((int32_t)level < q->min_level)
        return;
    UA_String formatted = UA_STRING_NULL;
    va_list copy;
    va_copy(copy, args);
    UA_StatusCode rc = UA_String_vformat(&formatted, msg, copy);
    va_end(copy);
    size_t len = rc == UA_STATUSCODE_GOOD ? formatted.length : strlen(msg);
    char *line = (char *)malloc(len + 1);
    if(!line) {
        UA_String_clear(&formatted);
        q->dropped++;
        return;
    }
    memcpy(line, rc == UA_STATUSCODE_GOOD ? (const char *)formatted.data : msg, len);
    line[len] = '\0';
    UA_String_clear(&formatted);
    logq_push(q, (int32_t)level, (int32_t)category, line);
}

static void logq_logger_clear(UA_Logger *logger) {
    free(logger); /* the queue itself is owned and cleared by the client/server wrapper */
}

UA_Logger *cictna_logq_logger(cictna_log_queue *q) {
    UA_Logger *logger = (UA_Logger *)calloc(1, sizeof(UA_Logger));
    if(!logger)
        return NULL;
    logger->log = logq_log;
    logger->context = q;
    logger->clear = logq_logger_clear;
    return logger;
}

int32_t cictna_logq_pop(cictna_log_queue *q, int32_t *level, int32_t *category,
                        const char **message, uint32_t *dropped) {
    free(q->current);
    q->current = NULL;
    *dropped = q->dropped;
    q->dropped = 0;
    if(q->count == 0)
        return 0;
    cictna_log_entry *e = &q->items[q->head];
    q->head = (q->head + 1) % q->capacity;
    q->count--;
    *level = e->level;
    *category = e->category;
    *message = e->message;
    q->current = e->message; /* handed out; freed on the next pop */
    e->message = NULL;
    return 1;
}
