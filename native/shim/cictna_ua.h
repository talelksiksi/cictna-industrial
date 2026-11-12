/*
 * cictna_ua: the C boundary between Cictna.Industrial.OpcUa.Native and open62541.
 *
 * Rules for this file:
 *  - Only plain C types cross the boundary (ints, pointers, UTF-8 strings).
 *  - Every function that can fail returns an OPC UA status code (uint32, 0 = Good).
 *  - Strings returned as const char* are static and must not be freed.
 *  - Bump CICTNA_UA_ABI_VERSION whenever a signature changes. The managed side
 *    refuses to load a library with a different ABI version.
 */
#ifndef CICTNA_UA_H
#define CICTNA_UA_H

#include <stdint.h>

#if defined(_WIN32)
#  define CICTNA_UA_API __declspec(dllexport)
#else
#  define CICTNA_UA_API __attribute__((visibility("default")))
#endif

#define CICTNA_UA_ABI_VERSION 1

/* Feature flags returned by cictna_ua_features(). */
#define CICTNA_UA_FEATURE_ENCRYPTION      0x0001u
#define CICTNA_UA_FEATURE_MULTITHREADING  0x0002u
#define CICTNA_UA_FEATURE_SUBSCRIPTIONS   0x0004u
#define CICTNA_UA_FEATURE_METHODCALLS     0x0008u
#define CICTNA_UA_FEATURE_HISTORIZING     0x0010u

#ifdef __cplusplus
extern "C" {
#endif

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

#ifdef __cplusplus
}
#endif

#endif /* CICTNA_UA_H */
