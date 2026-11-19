/*
 * cictna_ua: the C boundary between Cictna.Industrial.OpcUa.Native and open62541.
 *
 * Rules for this file:
 *  - Only plain C types cross the boundary (ints, pointers, UTF-8 strings, byte buffers).
 *  - Structured OPC UA data (requests, responses, values, node ids) crosses as standard OPC UA
 *    binary encoding (Part 6). The shim decodes and encodes it with open62541's own codec, so
 *    the managed side never mirrors open62541 structs.
 *  - Every function that can fail returns an OPC UA status code (uint32, 0 = Good).
 *  - Strings returned as const char* are static and must not be freed, unless stated otherwise.
 *  - Buffers returned through uint8_t** are owned by the caller: free them with cictna_ua_free,
 *    unless the function says the buffer stays owned by the shim.
 *  - Client and server objects are NOT thread-safe: the managed side calls each object from one
 *    worker thread only. Nothing in this API calls back into managed code; results, state
 *    changes and log lines are collected by polling.
 *  - Bump CICTNA_UA_ABI_VERSION whenever a signature or struct changes. The managed side
 *    refuses to load a library with a different ABI version.
 */
#ifndef CICTNA_UA_H
#define CICTNA_UA_H

#include <stddef.h>
#include <stdint.h>

#if defined(_WIN32)
#  define CICTNA_UA_API __declspec(dllexport)
#else
#  define CICTNA_UA_API __attribute__((visibility("default")))
#endif

/* 1: Phase 0 (identity, self test). 2: Phase 1 (client, test server, codec, certificates, logs). */
#define CICTNA_UA_ABI_VERSION 3

/* Feature flags returned by cictna_ua_features(). */
#define CICTNA_UA_FEATURE_ENCRYPTION      0x0001u
#define CICTNA_UA_FEATURE_MULTITHREADING  0x0002u
#define CICTNA_UA_FEATURE_SUBSCRIPTIONS   0x0004u
#define CICTNA_UA_FEATURE_METHODCALLS     0x0008u
#define CICTNA_UA_FEATURE_HISTORIZING     0x0010u

#ifdef __cplusplus
extern "C" {
#endif

/* ==== identity and health =================================================== */

/* ABI version of this shim (CICTNA_UA_ABI_VERSION). */
CICTNA_UA_API int32_t cictna_ua_abi_version(void);

/* open62541 version string, for example "v1.5.10". */
CICTNA_UA_API const char *cictna_ua_stack_version(void);

/* Encryption backend compiled in: "mbedtls", "openssl" or "none". */
CICTNA_UA_API const char *cictna_ua_encryption_backend(void);

/* Bitmask of CICTNA_UA_FEATURE_* flags. */
CICTNA_UA_API uint32_t cictna_ua_features(void);

/* Human-readable name of a status code, for example "BadTimeout". */
CICTNA_UA_API const char *cictna_ua_status_name(uint32_t status);

/*
 * Self test: starts a server on 127.0.0.1:port, connects a client to it,
 * reads Server_ServerStatus_CurrentTime and shuts both down, all on the
 * calling thread. Returns 0 on success, otherwise the first bad status code.
 * Writes the read value (OPC UA DateTime, 100 ns ticks since 1601) to out_time.
 */
CICTNA_UA_API uint32_t cictna_ua_selftest_loopback(uint16_t port, uint32_t timeout_ms,
                                                   int64_t *out_time);

/* ==== memory and codec ======================================================= */

/* Frees a buffer returned by this library. NULL is ignored. */
CICTNA_UA_API void cictna_ua_free(void *buffer);

/*
 * Decodes `in` with open62541 as the type identified by `type_id` (a namespace-0 DataType id
 * such as 24 for Variant, or a DefaultBinary encoding id such as 631 for ReadRequest), encodes
 * it again and returns the bytes in *out (free with cictna_ua_free). *consumed receives the
 * number of input bytes the decoder used. Used by the differential tests that check the C#
 * codec against open62541 byte for byte.
 */
CICTNA_UA_API uint32_t cictna_ua_codec_roundtrip(uint32_t type_id, const uint8_t *in, size_t in_len,
                                                 uint8_t **out, size_t *out_len, size_t *consumed);

/* ==== certificates ============================================================ */

/*
 * Creates a self-signed RSA application instance certificate (DER) and its private key (DER).
 * The subject is "CN=<common_name>, O=<organization>"; the subject alternative names are the
 * application URI and the DNS name. key_size_bits: 2048 or 4096. Free both with cictna_ua_free.
 */
CICTNA_UA_API uint32_t cictna_ua_create_certificate(const char *common_name, const char *organization,
                                                    const char *application_uri, const char *dns_name,
                                                    uint32_t key_size_bits, uint32_t valid_days,
                                                    uint8_t **cert, size_t *cert_len,
                                                    uint8_t **key, size_t *key_len);

/* ==== logging (shared by clients and servers) ================================= */

/* Log levels, identical to open62541's UA_LogLevel values. */
#define CICTNA_UA_LOG_TRACE   100
#define CICTNA_UA_LOG_DEBUG   200
#define CICTNA_UA_LOG_INFO    300
#define CICTNA_UA_LOG_WARNING 400
#define CICTNA_UA_LOG_ERROR   500
#define CICTNA_UA_LOG_FATAL   600

/* ==== client ===================================================================== */

typedef struct cictna_ua_client cictna_ua_client;

/* Connection options. Zero means "default" for every numeric field. */
typedef struct {
    uint32_t struct_size;                  /* sizeof(cictna_ua_client_options) */
    uint32_t request_timeout_ms;           /* default 10000 */
    uint32_t secure_channel_lifetime_ms;   /* default open62541's (10 min) */
    uint32_t session_timeout_ms;           /* default open62541's (60 min) */
    int32_t security_mode;                 /* 0 any, 1 None, 2 Sign, 3 SignAndEncrypt */
    const char *security_policy_uri;       /* NULL: any policy matching the mode */
    const char *application_uri;           /* must match the certificate's URI */
    const char *application_name;
    const uint8_t *certificate;            /* DER; NULL allows only SecurityPolicy None */
    size_t certificate_len;
    const uint8_t *private_key;            /* DER */
    size_t private_key_len;
    const char *username;                  /* NULL: anonymous */
    const char *password;
    uint32_t accept_any_server_certificate;/* development only; trust lists come in Phase 3 */
    int32_t log_level;                     /* minimum level queued for the managed side */
} cictna_ua_client_options;

/* Creates a client. Nothing is connected yet. */
CICTNA_UA_API uint32_t cictna_ua_client_new(const cictna_ua_client_options *options,
                                            cictna_ua_client **client);

/* Disconnects (without waiting) and frees everything, including undelivered completions. */
CICTNA_UA_API void cictna_ua_client_delete(cictna_ua_client *client);

/*
 * Starts connecting (secure channel, then session). Progress shows in cictna_ua_client_state.
 * `endpoint` is the binary-encoded EndpointDescription chosen by the caller (from GetEndpoints):
 * open62541 then opens the channel with exactly that security mode and policy, pins the server
 * certificate it contains, and skips its own endpoint discovery. The TCP connection always goes
 * to endpoint_url, not to the URL inside the description (servers often advertise host names
 * that clients cannot resolve). user_token_type selects the endpoint's user token policy:
 * 0 anonymous, 1 user name.
 */
CICTNA_UA_API uint32_t cictna_ua_client_connect(cictna_ua_client *client, const char *endpoint_url,
                                                const uint8_t *endpoint, size_t endpoint_len,
                                                int32_t user_token_type);

/* Starts a clean disconnect (close session, close channel). Closing the session also deletes its
 * subscriptions on the server. */
CICTNA_UA_API uint32_t cictna_ua_client_disconnect(cictna_ua_client *client);

/* Closes only the secure channel and leaves the session alone (ABI 3). Used before deleting a
 * client whose connection is in doubt: the session and its subscriptions stay on the server until
 * their timeouts, so a new client can take the subscriptions over with TransferSubscriptions. A
 * normal disconnect (or deleting a connected client) would send CloseSession and delete them. */
CICTNA_UA_API uint32_t cictna_ua_client_close_channel(cictna_ua_client *client);

/* Runs the client's event loop for at most timeout_ms. All network I/O, timeouts and response
 * handling happen inside this call. Call it continuously from the worker thread. */
CICTNA_UA_API uint32_t cictna_ua_client_iterate(cictna_ua_client *client, uint32_t timeout_ms);

/* Current state: UA_SecureChannelState, UA_SessionState and the connect status
 * (Good while connecting or connected, the failure reason otherwise). */
CICTNA_UA_API void cictna_ua_client_state(cictna_ua_client *client, int32_t *channel_state,
                                          int32_t *session_state, uint32_t *connect_status);

/*
 * Sends any service request asynchronously. `request` is the binary-encoded request message
 * (the request header's token, timestamp and handle are filled in by open62541).
 * request_encoding_id / response_encoding_id are the DefaultBinary encoding ids (for example
 * 631 / 634 for Read). The response arrives later through cictna_ua_client_poll with the same
 * request id. Every request that was sent gets exactly one response, also on timeout,
 * cancellation or disconnect (then with a bad service result in its response header).
 */
CICTNA_UA_API uint32_t cictna_ua_client_send(cictna_ua_client *client, uint32_t request_encoding_id,
                                             uint32_t response_encoding_id, const uint8_t *request,
                                             size_t request_len, uint32_t *request_id);

/* Asks the server to cancel a pending request (Cancel service). The response still arrives. */
CICTNA_UA_API uint32_t cictna_ua_client_cancel(cictna_ua_client *client, uint32_t request_id);

/*
 * Takes the next completed response. Returns 1 when one was available, 0 otherwise.
 * *status is Good unless the shim itself failed to encode the response. The buffer stays owned
 * by the shim and is valid until the next poll call or cictna_ua_client_delete.
 */
CICTNA_UA_API int32_t cictna_ua_client_poll(cictna_ua_client *client, uint32_t *request_id,
                                            uint32_t *status, const uint8_t **response,
                                            size_t *response_len);

/* Takes the next queued log line. Returns 1 when one was available. *message is UTF-8, owned by
 * the shim and valid until the next call. *dropped counts lines lost because the queue was full. */
CICTNA_UA_API int32_t cictna_ua_client_poll_log(cictna_ua_client *client, int32_t *level,
                                                int32_t *category, const char **message,
                                                uint32_t *dropped);

/* Synchronous GetEndpoints on its own temporary connection (the client must not be connected).
 * Returns a binary-encoded GetEndpointsResponse in *response (free with cictna_ua_free). */
CICTNA_UA_API uint32_t cictna_ua_client_get_endpoints(cictna_ua_client *client, const char *url,
                                                      uint8_t **response, size_t *response_len);

/* ==== server ===================================================================== */

typedef struct cictna_ua_server cictna_ua_server;

typedef struct {
    uint32_t struct_size;                  /* sizeof(cictna_ua_server_options) */
    uint16_t port;
    uint16_t reserved;
    const char *application_uri;
    const char *application_name;
    const uint8_t *certificate;            /* DER; NULL serves SecurityPolicy None only */
    size_t certificate_len;
    const uint8_t *private_key;
    size_t private_key_len;
    uint32_t allow_anonymous;
    const char *username;                  /* one user name/password login, NULL for none */
    const char *password;
    uint32_t max_nodes_per_read;           /* 0: open62541 default */
    uint32_t max_nodes_per_write;
    uint32_t max_nodes_per_browse;
    uint32_t max_references_per_node;
    int32_t log_level;
    uint32_t max_monitored_item_queue_size; /* ABI 3. Upper limit for a monitored item's queue;
                                             * 0: open62541 default (100) */
} cictna_ua_server_options;

CICTNA_UA_API uint32_t cictna_ua_server_new(const cictna_ua_server_options *options,
                                            cictna_ua_server **server);

/* Opens the listen socket. */
CICTNA_UA_API uint32_t cictna_ua_server_start(cictna_ua_server *server);

/* Runs one iteration of the server's event loop without blocking. *next_ms receives the time
 * until the server next needs to run (call again sooner if there is work). */
CICTNA_UA_API uint32_t cictna_ua_server_iterate(cictna_ua_server *server, uint32_t *next_ms);

/* Closes all sessions and the listen socket. The server can be deleted afterwards. */
CICTNA_UA_API uint32_t cictna_ua_server_stop(cictna_ua_server *server);

CICTNA_UA_API void cictna_ua_server_delete(cictna_ua_server *server);

/* Registers a namespace URI and returns its index. */
CICTNA_UA_API uint32_t cictna_ua_server_add_namespace(cictna_ua_server *server, const char *uri,
                                                      uint16_t *index);

/* Adds an Object or Variable node from a binary-encoded AddNodesItem (nodeAttributes holding
 * ObjectAttributes or VariableAttributes). Returns the binary-encoded NodeId of the new node. */
CICTNA_UA_API uint32_t cictna_ua_server_add_node(cictna_ua_server *server, const uint8_t *item,
                                                 size_t item_len, uint8_t **node_id,
                                                 size_t *node_id_len);

/* Writes a binary-encoded WriteValue locally (no session, no access checks). */
CICTNA_UA_API uint32_t cictna_ua_server_write(cictna_ua_server *server, const uint8_t *write_value,
                                              size_t len);

/* Reads a binary-encoded ReadValueId locally and returns a binary-encoded DataValue. */
CICTNA_UA_API uint32_t cictna_ua_server_read(cictna_ua_server *server, const uint8_t *read_value_id,
                                             size_t len, int32_t timestamps, uint8_t **data_value,
                                             size_t *data_value_len);

/* Same contract as cictna_ua_client_poll_log. */
CICTNA_UA_API int32_t cictna_ua_server_poll_log(cictna_ua_server *server, int32_t *level,
                                                int32_t *category, const char **message,
                                                uint32_t *dropped);

#ifdef __cplusplus
}
#endif

#endif /* CICTNA_UA_H */
