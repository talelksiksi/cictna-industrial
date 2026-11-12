/*
 * Native smoke test: loads libcictna_ua with dlopen (no .NET needed), prints the
 * stack version and runs the loopback self test. Used in CI for linux-arm64 under
 * qemu, where no ARM64 .NET runtime is available.
 *
 *   gcc -o native-smoke native/tests/native_smoke.c -ldl
 *   ./native-smoke native/out/linux-x64/libcictna_ua.so [port]
 */
#include <dlfcn.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef int32_t (*abi_fn)(void);
typedef const char *(*str_fn)(void);
typedef const char *(*status_name_fn)(uint32_t);
typedef uint32_t (*loopback_fn)(uint16_t, uint32_t, int64_t *);

int main(int argc, char **argv) {
    if(argc < 2) {
        fprintf(stderr, "usage: %s <path to libcictna_ua.so> [port]\n", argv[0]);
        return 2;
    }
    void *lib = dlopen(argv[1], RTLD_NOW | RTLD_LOCAL);
    if(!lib) {
        fprintf(stderr, "FAIL load: %s\n", dlerror());
        return 1;
    }
    abi_fn abi = (abi_fn)dlsym(lib, "cictna_ua_abi_version");
    str_fn version = (str_fn)dlsym(lib, "cictna_ua_stack_version");
    str_fn crypto = (str_fn)dlsym(lib, "cictna_ua_encryption_backend");
    status_name_fn status_name = (status_name_fn)dlsym(lib, "cictna_ua_status_name");
    loopback_fn loopback = (loopback_fn)dlsym(lib, "cictna_ua_selftest_loopback");
    if(!abi || !version || !crypto || !status_name || !loopback) {
        fprintf(stderr, "FAIL missing export\n");
        return 1;
    }

    uint16_t port = argc > 2 ? (uint16_t)atoi(argv[2]) : 48401;
    int64_t server_time = 0;
    uint32_t rc = loopback(port, 10000, &server_time);
    printf("abi=%d open62541=%s crypto=%s loopback=%s\n",
           abi(), version(), crypto(), status_name(rc));

    int ok = rc == 0 && server_time > 0 && strcmp(crypto(), "mbedtls") == 0;
    puts(ok ? "NATIVE SMOKE OK" : "NATIVE SMOKE FAILED");
    return ok ? 0 : 1;
}
