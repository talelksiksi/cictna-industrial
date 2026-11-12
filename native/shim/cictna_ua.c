#include "cictna_ua.h"

#include <open62541/client.h>
#include <open62541/client_config_default.h>
#include <open62541/client_highlevel_async.h>
#include <open62541/plugin/log_stdout.h>
#include <open62541/server.h>
#include <open62541/server_config_default.h>

#include <stdio.h>
#include <string.h>

int32_t cictna_ua_abi_version(void) {
    return CICTNA_UA_ABI_VERSION;
}

const char *cictna_ua_stack_version(void) {
    return UA_OPEN62541_VERSION;
}

const char *cictna_ua_encryption_backend(void) {
#if defined(UA_ENABLE_ENCRYPTION_MBEDTLS)
    return "mbedtls";
#elif defined(UA_ENABLE_ENCRYPTION_OPENSSL)
    return "openssl";
#else
    return "none";
#endif
}

uint32_t cictna_ua_features(void) {
    uint32_t f = 0;
#ifdef UA_ENABLE_ENCRYPTION
    f |= CICTNA_UA_FEATURE_ENCRYPTION;
#endif
#if UA_MULTITHREADING >= 100
    f |= CICTNA_UA_FEATURE_MULTITHREADING;
#endif
#ifdef UA_ENABLE_SUBSCRIPTIONS
    f |= CICTNA_UA_FEATURE_SUBSCRIPTIONS;
#endif
#ifdef UA_ENABLE_METHODCALLS
    f |= CICTNA_UA_FEATURE_METHODCALLS;
#endif
#ifdef UA_ENABLE_HISTORIZING
    f |= CICTNA_UA_FEATURE_HISTORIZING;
#endif
    return f;
}

const char *cictna_ua_status_name(uint32_t status) {
    return UA_StatusCode_name(status);
}

/* ---- loopback self test ------------------------------------------------ */

typedef struct {
    int done;
    UA_StatusCode status;
    int64_t time;
} ReadResult;

static void on_read(UA_Client *client, void *userdata, UA_UInt32 requestId,
                    UA_StatusCode status, UA_DataValue *value) {
    (void)client;
    (void)requestId;
    ReadResult *r = (ReadResult *)userdata;
    r->done = 1;
    r->status = status;
    if(status != UA_STATUSCODE_GOOD)
        return;
    if(!value || !value->hasValue ||
       !UA_Variant_hasScalarType(&value->value, &UA_TYPES[UA_TYPES_DATETIME])) {
        r->status = UA_STATUSCODE_BADTYPEMISMATCH;
        return;
    }
    r->time = *(UA_DateTime *)value->value.data;
}

uint32_t cictna_ua_selftest_loopback(uint16_t port, uint32_t timeout_ms, int64_t *out_time) {
    UA_StatusCode rc = UA_STATUSCODE_GOOD;
    UA_Server *server = NULL;
    UA_Client *client = NULL;
    int server_started = 0;

    if(out_time)
        *out_time = 0;

    UA_ServerConfig sc;
    memset(&sc, 0, sizeof(sc));
    sc.logging = UA_Log_Stdout_new(UA_LOGLEVEL_ERROR);
    rc = UA_ServerConfig_setMinimal(&sc, port, NULL);
    if(rc != UA_STATUSCODE_GOOD) {
        UA_ServerConfig_clear(&sc);
        return rc;
    }
    server = UA_Server_newWithConfig(&sc);
    if(!server)
        return UA_STATUSCODE_BADOUTOFMEMORY;

    rc = UA_Server_run_startup(server);
    if(rc != UA_STATUSCODE_GOOD)
        goto cleanup;
    server_started = 1;

    UA_ClientConfig cc;
    memset(&cc, 0, sizeof(cc));
    cc.logging = UA_Log_Stdout_new(UA_LOGLEVEL_ERROR);
    rc = UA_ClientConfig_setDefault(&cc);
    if(rc != UA_STATUSCODE_GOOD) {
        UA_ClientConfig_clear(&cc);
        goto cleanup;
    }
    cc.timeout = timeout_ms;
    client = UA_Client_newWithConfig(&cc);
    if(!client) {
        rc = UA_STATUSCODE_BADOUTOFMEMORY;
        goto cleanup;
    }

    char url[64];
    snprintf(url, sizeof(url), "opc.tcp://127.0.0.1:%u", (unsigned)port);
    rc = UA_Client_connectAsync(client, url);
    if(rc != UA_STATUSCODE_GOOD)
        goto cleanup;

    /* Drive both event loops on this thread until the session is active and
     * the read has completed, or the deadline passes. */
    UA_DateTime deadline = UA_DateTime_nowMonotonic() +
                           (UA_DateTime)timeout_ms * UA_DATETIME_MSEC;
    ReadResult rr = {0, UA_STATUSCODE_GOOD, 0};
    int read_sent = 0;
    while(!rr.done) {
        if(UA_DateTime_nowMonotonic() > deadline) {
            rc = UA_STATUSCODE_BADTIMEOUT;
            goto cleanup;
        }
        UA_Server_run_iterate(server, false);
        UA_StatusCode it = UA_Client_run_iterate(client, 1);
        UA_SessionState session;
        UA_StatusCode connect_status;
        UA_Client_getState(client, NULL, &session, &connect_status);
        if(connect_status != UA_STATUSCODE_GOOD) {
            rc = connect_status;
            goto cleanup;
        }
        if(it != UA_STATUSCODE_GOOD) {
            rc = it;
            goto cleanup;
        }
        if(!read_sent && session == UA_SESSIONSTATE_ACTIVATED) {
            rc = UA_Client_readValueAttribute_async(
                client, UA_NODEID_NUMERIC(0, UA_NS0ID_SERVER_SERVERSTATUS_CURRENTTIME),
                on_read, &rr, NULL);
            if(rc != UA_STATUSCODE_GOOD)
                goto cleanup;
            read_sent = 1;
        }
    }
    rc = rr.status;
    if(rc == UA_STATUSCODE_GOOD && out_time)
        *out_time = rr.time;

cleanup:
    if(client) {
        /* Disconnect needs the server to answer CloseSession, so keep the
         * server loop running while the client closes. */
        UA_Client_disconnectAsync(client);
        for(int i = 0; i < 200; i++) {
            UA_SecureChannelState channel;
            UA_Client_getState(client, &channel, NULL, NULL);
            if(channel == UA_SECURECHANNELSTATE_CLOSED)
                break;
            UA_Server_run_iterate(server, false);
            UA_Client_run_iterate(client, 1);
        }
        UA_Client_delete(client);
    }
    if(server) {
        if(server_started)
            UA_Server_run_shutdown(server);
        UA_Server_delete(server);
    }
    return rc;
}
