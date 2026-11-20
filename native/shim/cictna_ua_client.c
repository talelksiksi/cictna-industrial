/*
 * cictna_ua_client.c: the OPC UA client behind cictna_ua_client_*.
 *
 * Model
 * -----
 * One cictna_ua_client wraps one UA_Client and is driven by exactly one managed worker thread:
 *
 *   worker loop:  send/connect/... (commands)  ->  cictna_ua_client_iterate  ->  poll completions,
 *                                                                                 state and logs
 *
 * Requests are sent with open62541's generic asynchronous service call. Its callback runs inside
 * cictna_ua_client_iterate (same thread) and only encodes the response and appends it to a FIFO;
 * the managed side drains that FIFO with cictna_ua_client_poll. Nothing here ever calls into
 * managed code, which avoids reverse P/Invoke, delegate lifetime problems and exceptions
 * crossing native frames.
 *
 * Reconnection: open62541 can reconnect on its own. We switch that off (noReconnect) so a lost
 * connection is reported plainly; the managed layer reconnects with backoff, using a fresh native
 * client for every connection, and moves subscriptions over with TransferSubscriptions.
 */
#include "cictna_ua_internal.h"

#include <open62541/client.h>
#include <open62541/client_config_default.h>
#include <open62541/client_highlevel_async.h>
#include <open62541/plugin/certificategroup_default.h>

#include <stdlib.h>
#include <string.h>

typedef struct {
    uint32_t request_id;
    uint32_t status;
    UA_ByteString data;
} completion;

struct cictna_ua_client {
    UA_Client *client;
    /* FIFO of completed responses (ring buffer that grows when full). */
    completion *items;
    size_t capacity;
    size_t head;
    size_t count;
    completion current;     /* handed out by the last poll, freed at the next */
    int deleting;           /* set during delete: late callbacks only free memory */
    cictna_log_queue log;
};

/* Per-request context for the callback: who to deliver to and how to encode the response. */
typedef struct {
    cictna_ua_client *owner;
    const UA_DataType *response_type;
} pending_request;

/* Keeps open62541 on the URL the caller gave.
 *
 * While connecting, open62541 asks the server for its discovery URLs (FindServers). If the URL
 * we dialled is not among them, it drops the channel and reconnects to the URL the server
 * advertises. On real networks that is often wrong: behind NAT, a port forward, a Docker port
 * mapping or a proxy, the server only knows its own internal name and port (node-opcua, for
 * one, advertises its machine name). The connection then fails, or silently bypasses the path
 * the user chose.
 *
 * open62541 offers one way to steer this: config.applicationUri filters the servers FindServers
 * considers (and the endpoints of its own endpoint selection, which we never use: the endpoint is
 * always preset). When no server matches the filter, open62541 keeps the original URL. So we set
 * a filter that no server can match. Found by the node-opcua interop tests through a TCP proxy. */
static const char *const KEEP_GIVEN_URL_FILTER = "urn:cictna:keep-the-given-endpoint-url";

static void push_completion(cictna_ua_client *c, uint32_t request_id, uint32_t status, UA_ByteString data) {
    if(c->count == c->capacity) {
        size_t new_cap = c->capacity ? c->capacity * 2 : 16;
        completion *items = (completion *)calloc(new_cap, sizeof(completion));
        if(!items) {
            /* Out of memory: the managed side would wait forever for this response; its own
             * request timeout then fails the call. Free what we have and give up. */
            UA_ByteString_clear(&data);
            return;
        }
        for(size_t i = 0; i < c->count; i++)
            items[i] = c->items[(c->head + i) % c->capacity];
        free(c->items);
        c->items = items;
        c->capacity = new_cap;
        c->head = 0;
    }
    completion *slot = &c->items[(c->head + c->count) % c->capacity];
    slot->request_id = request_id;
    slot->status = status;
    slot->data = data;
    c->count++;
}

static void on_response(UA_Client *client, void *userdata, UA_UInt32 request_id, void *response) {
    (void)client;
    pending_request *p = (pending_request *)userdata;
    cictna_ua_client *c = p->owner;
    const UA_DataType *type = p->response_type;
    free(p);
    if(c->deleting)
        return;
    UA_ByteString data = UA_BYTESTRING_NULL;
    UA_StatusCode rc = UA_encodeBinary(response, type, &data, NULL);
    if(rc != UA_STATUSCODE_GOOD)
        UA_ByteString_clear(&data);
    push_completion(c, request_id, rc, data);
}

uint32_t cictna_ua_client_new(const cictna_ua_client_options *o, cictna_ua_client **out) {
    *out = NULL;
    if(!o || o->struct_size != sizeof(cictna_ua_client_options))
        return UA_STATUSCODE_BADINVALIDARGUMENT;

    cictna_ua_client *c = (cictna_ua_client *)calloc(1, sizeof(cictna_ua_client));
    if(!c)
        return UA_STATUSCODE_BADOUTOFMEMORY;
    cictna_logq_init(&c->log, 1024, o->log_level);

    UA_ClientConfig cc;
    memset(&cc, 0, sizeof(cc));
    cc.logging = cictna_logq_logger(&c->log);

    UA_StatusCode rc;
#ifdef UA_ENABLE_ENCRYPTION
    if(o->certificate && o->certificate_len && o->private_key && o->private_key_len) {
        /* Registers None plus every secure policy. Empty trust list for now: server certificate
         * validation is configured below (accept-any in development) and properly in Phase 3. */
        UA_ByteString cert = {o->certificate_len, (UA_Byte *)(uintptr_t)o->certificate};
        UA_ByteString key = {o->private_key_len, (UA_Byte *)(uintptr_t)o->private_key};
        rc = UA_ClientConfig_setDefaultEncryption(&cc, cert, key, NULL, 0, NULL, 0);
        if(rc == UA_STATUSCODE_GOOD) {
            /* Server certificate validation. With no trust list open62541 keeps its default,
             * which accepts every certificate. That is never acceptable outside a lab, so
             * unless the caller explicitly asked for accept-any (development), the in-memory
             * store is filled with the caller's trust list; an empty one rejects everything.
             * Secure by default. The store checks the chain, validity period and CRLs
             * (Part 6, 6.1.3). */
            if(o->accept_any_server_certificate) {
                if(cc.certificateVerification.clear)
                    cc.certificateVerification.clear(&cc.certificateVerification);
                UA_CertificateGroup_AcceptAll(&cc.certificateVerification);
            } else {
                rc = cictna_memorystore(&cc.certificateVerification, o->trust_list, o->trust_list_len,
                                        cc.logging);
            }
        }
    } else
#endif
    {
        rc = UA_ClientConfig_setDefault(&cc);
    }
    if(rc != UA_STATUSCODE_GOOD)
        goto fail;

    cc.timeout = o->request_timeout_ms ? o->request_timeout_ms : 10000;
    cc.applicationUri = UA_STRING_ALLOC(KEEP_GIVEN_URL_FILTER);
    if(o->secure_channel_lifetime_ms)
        cc.secureChannelLifeTime = o->secure_channel_lifetime_ms;
    if(o->session_timeout_ms)
        cc.requestedSessionTimeout = o->session_timeout_ms;
    cc.securityMode = (UA_MessageSecurityMode)o->security_mode; /* 0 = INVALID = any */
    if(o->security_policy_uri) {
        UA_String_clear(&cc.securityPolicyUri);
        cc.securityPolicyUri = cictna_string(o->security_policy_uri);
    }
    if(o->application_uri) {
        UA_String_clear(&cc.clientDescription.applicationUri);
        cc.clientDescription.applicationUri = cictna_string(o->application_uri);
    }
    if(o->application_name) {
        UA_LocalizedText_clear(&cc.clientDescription.applicationName);
        cc.clientDescription.applicationName = UA_LOCALIZEDTEXT_ALLOC("", o->application_name);
    }
    if(o->username) {
        rc = UA_ClientConfig_setAuthenticationUsername(&cc, o->username, o->password ? o->password : "");
        if(rc != UA_STATUSCODE_GOOD)
            goto fail;
    }
    cc.noReconnect = true; /* see the file header */

    c->client = UA_Client_newWithConfig(&cc); /* moves cc into the client, also on failure */
    if(!c->client) {
        cictna_logq_clear(&c->log);
        free(c);
        return UA_STATUSCODE_BADOUTOFMEMORY;
    }
    *out = c;
    return UA_STATUSCODE_GOOD;

fail:
    UA_ClientConfig_clear(&cc);
    cictna_logq_clear(&c->log);
    free(c);
    return rc;
}

void cictna_ua_client_delete(cictna_ua_client *c) {
    if(!c)
        return;
    /* Pending requests are cancelled inside UA_Client_delete and their callbacks fire; the
     * deleting flag makes them free their context without queueing anything. */
    c->deleting = 1;
    UA_Client_delete(c->client);
    for(size_t i = 0; i < c->count; i++)
        UA_ByteString_clear(&c->items[(c->head + i) % c->capacity].data);
    free(c->items);
    UA_ByteString_clear(&c->current.data);
    cictna_logq_clear(&c->log); /* after UA_Client_delete: it may still log while closing */
    free(c);
}

uint32_t cictna_ua_client_connect(cictna_ua_client *c, const char *endpoint_url,
                                  const uint8_t *endpoint, size_t endpoint_len,
                                  int32_t user_token_type) {
    if(!endpoint_url || !endpoint || endpoint_len == 0)
        return UA_STATUSCODE_BADINVALIDARGUMENT;

    UA_StatusCode rc;
    UA_EndpointDescription *ep = (UA_EndpointDescription *)
        cictna_decode(&UA_TYPES[UA_TYPES_ENDPOINTDESCRIPTION], endpoint, endpoint_len, &rc);
    if(!ep)
        return rc;

    /* The user token policy of the endpoint that matches the identity we will present. */
    const UA_UserTokenPolicy *token = NULL;
    for(size_t i = 0; i < ep->userIdentityTokensSize; i++) {
        if((int32_t)ep->userIdentityTokens[i].tokenType == user_token_type) {
            token = &ep->userIdentityTokens[i];
            break;
        }
    }
    if(!token) {
        UA_EndpointDescription_delete(ep);
        return UA_STATUSCODE_BADIDENTITYTOKENINVALID;
    }

    /* Preset endpoint and token policy: open62541 uses them as given ("direct definition") and
     * does not run its own endpoint selection, which would otherwise close and reopen the
     * channel to switch security, something it cannot do with noReconnect set. */
    UA_ClientConfig *cfg = UA_Client_getConfig(c->client);
    UA_UserTokenPolicy_clear(&cfg->userTokenPolicy);
    rc = UA_UserTokenPolicy_copy(token, &cfg->userTokenPolicy);
    UA_EndpointDescription_clear(&cfg->endpoint);
    cfg->endpoint = *ep;           /* move the contents */
    UA_free(ep);                   /* only the shell, the contents now belong to cfg */
    if(rc != UA_STATUSCODE_GOOD)
        return rc;
    return UA_Client_connectAsync(c->client, endpoint_url);
}

uint32_t cictna_ua_client_disconnect(cictna_ua_client *c) {
    return UA_Client_disconnectAsync(c->client);
}

uint32_t cictna_ua_client_close_channel(cictna_ua_client *c) {
    /* Synchronous: when this returns the channel is closed, so a following UA_Client_delete finds
     * no channel to send CloseSession on and the server keeps the session (checked in
     * open62541's ua_client_connect.c: UA_Client_disconnect only sends CloseSession over an open
     * channel). */
    return UA_Client_disconnectSecureChannel(c->client);
}

uint32_t cictna_ua_client_iterate(cictna_ua_client *c, uint32_t timeout_ms) {
    return UA_Client_run_iterate(c->client, timeout_ms);
}

void cictna_ua_client_state(cictna_ua_client *c, int32_t *channel_state, int32_t *session_state,
                            uint32_t *connect_status) {
    UA_SecureChannelState channel = UA_SECURECHANNELSTATE_CLOSED;
    UA_SessionState session = UA_SESSIONSTATE_CLOSED;
    UA_StatusCode status = UA_STATUSCODE_GOOD;
    UA_Client_getState(c->client, &channel, &session, &status);
    *channel_state = (int32_t)channel;
    *session_state = (int32_t)session;
    *connect_status = status;
}

uint32_t cictna_ua_client_send(cictna_ua_client *c, uint32_t request_encoding_id,
                               uint32_t response_encoding_id, const uint8_t *request,
                               size_t request_len, uint32_t *request_id) {
    *request_id = 0;
    const UA_DataType *req_type = cictna_find_type(request_encoding_id);
    const UA_DataType *resp_type = cictna_find_type(response_encoding_id);
    if(!req_type || !resp_type)
        return UA_STATUSCODE_BADDATATYPEIDUNKNOWN;

    UA_StatusCode rc;
    void *req = cictna_decode(req_type, request, request_len, &rc);
    if(!req)
        return rc;

    pending_request *p = (pending_request *)malloc(sizeof(pending_request));
    if(!p) {
        UA_delete(req, req_type);
        return UA_STATUSCODE_BADOUTOFMEMORY;
    }
    p->owner = c;
    p->response_type = resp_type;

    /* The request is encoded onto the wire inside this call, so it can be freed right after.
     * On failure the callback is never called (checked in open62541's source), so p is ours. */
    rc = __UA_Client_AsyncService(c->client, req, req_type, on_response, resp_type, p, request_id);
    UA_delete(req, req_type);
    if(rc != UA_STATUSCODE_GOOD)
        free(p);
    return rc;
}

uint32_t cictna_ua_client_cancel(cictna_ua_client *c, uint32_t request_id) {
    return UA_Client_cancelByRequestId(c->client, request_id, NULL);
}

int32_t cictna_ua_client_poll(cictna_ua_client *c, uint32_t *request_id, uint32_t *status,
                              const uint8_t **response, size_t *response_len) {
    UA_ByteString_clear(&c->current.data);
    memset(&c->current, 0, sizeof(c->current));
    if(c->count == 0)
        return 0;
    c->current = c->items[c->head];
    memset(&c->items[c->head], 0, sizeof(completion));
    c->head = (c->head + 1) % c->capacity;
    c->count--;
    *request_id = c->current.request_id;
    *status = c->current.status;
    *response = c->current.data.data;
    *response_len = c->current.data.length;
    return 1;
}

int32_t cictna_ua_client_poll_log(cictna_ua_client *c, int32_t *level, int32_t *category,
                                  const char **message, uint32_t *dropped) {
    return cictna_logq_pop(&c->log, level, category, message, dropped);
}

uint32_t cictna_ua_client_get_endpoints(cictna_ua_client *c, const char *url, uint8_t **response,
                                        size_t *response_len) {
    *response = NULL;
    *response_len = 0;
    if(!url)
        return UA_STATUSCODE_BADINVALIDARGUMENT;

    /* Discovery runs on its own temporary client with the plain default configuration
     * (SecurityPolicy None, no certificate). GetEndpoints never needs security, and the main
     * client's security settings would make open62541 try to switch the discovery channel to an
     * encrypted one, which fails with noReconnect. Its log lines go to the same queue. */
    UA_ClientConfig tc;
    memset(&tc, 0, sizeof(tc));
    tc.logging = cictna_logq_logger(&c->log);
    UA_StatusCode rc = UA_ClientConfig_setDefault(&tc);
    if(rc != UA_STATUSCODE_GOOD) {
        UA_ClientConfig_clear(&tc);
        return rc;
    }
    tc.timeout = UA_Client_getConfig(c->client)->timeout;
    tc.noReconnect = true;
    tc.applicationUri = UA_STRING_ALLOC(KEEP_GIVEN_URL_FILTER);
    /* Preset a plain endpoint so open62541 skips its own endpoint selection. That selection may
     * otherwise close the channel to reopen it on the URL the server advertises (node-opcua, for
     * one, advertises its machine name), which fails with noReconnect and, on plant networks,
     * often because that name does not resolve. Found by the node-opcua interop tests. */
    tc.endpoint.securityMode = UA_MESSAGESECURITYMODE_NONE;
    tc.endpoint.securityPolicyUri = UA_STRING_ALLOC("http://opcfoundation.org/UA/SecurityPolicy#None");
    tc.endpoint.endpointUrl = UA_STRING_ALLOC(url);
    UA_Client *discovery = UA_Client_newWithConfig(&tc);
    if(!discovery)
        return UA_STATUSCODE_BADOUTOFMEMORY;

    size_t count = 0;
    UA_EndpointDescription *endpoints = NULL;
    rc = UA_Client_getEndpoints(discovery, url, &count, &endpoints);
    UA_Client_delete(discovery);
    if(rc != UA_STATUSCODE_GOOD)
        return rc;

    /* Wrap the array in a GetEndpointsResponse so the managed side decodes one standard
     * message instead of a bare array. The array is moved in and freed with the response. */
    UA_GetEndpointsResponse msg;
    UA_GetEndpointsResponse_init(&msg);
    msg.responseHeader.timestamp = UA_DateTime_now();
    msg.endpoints = endpoints;
    msg.endpointsSize = count;
    rc = cictna_encode(&msg, &UA_TYPES[UA_TYPES_GETENDPOINTSRESPONSE], response, response_len);
    UA_GetEndpointsResponse_clear(&msg);
    return rc;
}
