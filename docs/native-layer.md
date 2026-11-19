# Native layer

How the OPC UA protocol core (open62541, in C) is built, packaged and called from C#.
Read this before touching anything under `native/` or `src/Cictna.Industrial.OpcUa.Native/`.

## The pieces

```
open62541 (C, MPL-2.0)   ─┐
mbedTLS   (C, Apache-2.0) ├─ static libs ─► cictna_ua (shared lib, our C shim) ─► P/Invoke ─► C#
our shim  (C, proprietary)┘                 exports only cictna_ua_*
```

| Path | What it is |
|---|---|
| `native/versions.env` | Pinned tag + commit of open62541 and mbedTLS. The only place versions change. |
| `native/build.sh` | Linux build (x64 native, ARM64 cross). Every CMake option is explained in it. |
| `native/build.ps1` | Windows build (MSVC, static C runtime). Same options as build.sh. |
| `native/shim/cictna_ua.h` | The C boundary. The contract between C and C#. |
| `native/shim/cictna_ua.c` | Implementation. All open62541 struct handling happens here. |
| `native/shim/CMakeLists.txt` | Links everything into one library and hides all non-`cictna_ua_*` symbols. |
| `native/toolchains/` | CMake toolchain for the ARM64 cross-compiler. |
| `native/tests/native_smoke.c` | Loads the library with `dlopen` and runs the self test (ARM64 under qemu in CI). |
| `native/licenses/` | Licence texts shipped in the NuGet package. Regenerate with `scripts/collect-native-licenses.py`. |
| `native/.deps/`, `.build/`, `out/` | Fetched sources, build trees, outputs. Git-ignored. |

## Why a C shim instead of calling open62541 directly

1. Large parts of open62541's API are `static inline` functions and macros. They have no exported
   symbol, so P/Invoke cannot call them.
2. Its structs change between versions and with build options. Mirroring them in C# would break
   silently on a version bump. In the shim they are compiled against the exact headers.
3. A small plain-C surface (ints, pointers, UTF-8 strings, status codes) is easy to keep stable,
   version (`CICTNA_UA_ABI_VERSION`) and test.

## Why one static library per platform

open62541 and mbedTLS are linked statically into `cictna_ua`, and the linker hides their symbols.
Each platform therefore ships one file that depends only on the C library:

- client machines need no OpenSSL, mbedTLS or VC++ redistributable;
- our open62541 cannot clash with another copy loaded in the same process;
- the package version fully determines the protocol stack version.

MPL-2.0 allows static linking with proprietary code as long as open62541's own files stay
unmodified or their changes are published. We never edit them.

## Build options that matter

See the comment block in `native/build.sh` for every option. The important ones:

- **Encryption: mbedTLS.** Enables Basic256Sha256 and the other Sign / SignAndEncrypt policies.
  Tests fail if a build comes out without it.
- **UA_MULTITHREADING=100.** open62541 locks internally around its public API. The C# layer still
  serializes calls per client; the lock is a safety net for callbacks.
- **Namespace zero REDUCED.** Enough for the client and the simulator's server. FULL would need the
  ua-nodeset submodule and is a Phase 5 decision.
- **Log level 300 (info).** Trace and debug logging are compiled out.

## Loading at run time

`NativeLibraryResolver` (registered by a module initializer before the first P/Invoke) looks in:

1. the runtime's default probing, which for NuGet consumers uses `deps.json` and finds
   `runtimes/<rid>/native/` automatically;
2. `runtimes/<rid>/native/` under the application folder, for project references and plain copies.

`OpcUaNativeLibrary.EnsureCompatible()` then checks the ABI version. Call it once at startup.

## ABI history

| ABI | Change |
|---|---|
| 1 | Identity functions and the loopback self test (Phase 0) |
| 2 | Generic service call, client and server objects, codec round trip, certificates, log queues (Phase 1) |
| 3 | `cictna_ua_client_close_channel`; `max_monitored_item_queue_size` in the server options (Phase 2) |

## Changing the C boundary

In one commit: edit `cictna_ua.h` and `cictna_ua.c`, mirror the change in `NativeMethods.cs`, bump
`CICTNA_UA_ABI_VERSION` and `OpcUaNativeLibrary.ExpectedAbiVersion`, add tests.

## Bumping open62541 or mbedTLS

1. Find the new tag and its commit: `git ls-remote --tags <url> <tag>`.
2. Update tag and commit in `native/versions.env`.
3. `native/build.sh linux-x64 --clean` (and arm64), run the tests.
4. `python3 scripts/collect-native-licenses.py`, then review `THIRD-PARTY-NOTICES.md`
   (versions, commits, the list of compiled-in dependencies).
5. Read the upstream release notes for API changes that touch the shim.
6. Run CI on Windows, then write a build record (`scripts/build-and-record.sh`).

## Known upstream quirks

Found by the differential codec tests (tests/Unit/Cictna.Industrial.OpcUa.Tests/Codec); our
codec follows open62541 where the specification leaves room, and works around the rest.

- Matrices: open62541 rejects array dimensions of 0. `Variant.Array` rejects them too, so such a
  value fails early with a clear message instead of deep inside a write.
- ExpandedNodeId: with a namespace URI the namespace index is forced to 0, and an empty URI counts
  as none (Part 4 wording). `ExpandedNodeId` normalises the same way on construction.
- An array of completely empty ExtensionObjects (null type id, no body) at the very end of a
  buffer is rejected by open62541's length check, which assumes at least 4 bytes per element
  while the empty form is 3. Harmless in real messages, which always continue after such an
  array; noted here in case it ever shows up in a log.

- Namespace mapping in the client: open62541 1.5 keeps a client-side namespace table filled from
  the server's NamespaceArray at the first connect, and translates indexes in every value. With
  no predefined namespaces (our setup) the mapping is the identity for all namespaces the server
  has; an index the server does not have is mapped to 65535 minus the index, so it can never
  silently point at the wrong namespace. The table is not re-read on a later connect of the same
  native client, so the managed client creates a fresh native client for every connection
  (Phase 2), and a server restarted with a different namespace order is handled correctly.

- Client certificate validation: with an empty trust list open62541 keeps its default, which
  accepts every certificate. The shim therefore installs an empty in-memory trust store unless
  AcceptAnyServerCertificateForDevelopment is set, so untrusted server certificates are rejected.
  The server side still accepts any client certificate; trust lists for both sides are Phase 3.
- Endpoint switching: open62541's own endpoint selection closes and reopens the channel to change
  security, which does not work with noReconnect. The client therefore runs GetEndpoints on a
  separate plain discovery client, chooses the endpoint in C# (EndpointSelector) and passes it in
  preset, so the secure channel is opened directly. The discovery client itself also gets a preset
  plain endpoint: otherwise open62541 reopens the discovery channel on the URL the server
  advertises (node-opcua advertises its machine name), which fails with noReconnect and, on plant
  networks, often because that name does not resolve. Found by the node-opcua interop tests.
- FindServers URL switch: while connecting, open62541 also asks the server for its discovery URLs
  and, if the URL we dialled is not among them, reconnects to the advertised one. Behind NAT, a port
  forward, a Docker port mapping or a proxy that is wrong (the server only knows its internal
  address). The only switch open62541 offers is `config.applicationUri`, a filter on which servers
  FindServers considers; the shim sets it to a value no server matches, and open62541 then keeps
  the given URL. The filter also applies to open62541's own endpoint selection, which we never use
  (the endpoint is always preset). Found by the node-opcua interop tests through a TCP proxy.
- Closing a client deletes its subscriptions: `UA_Client_disconnect` and `UA_Client_delete` send
  CloseSession with deleteSubscriptions=true while the session is active. When the managed client
  gives up on a connection it still believes alive (missed keep-alives), it first calls
  `cictna_ua_client_close_channel` (ABI 3), which closes only the secure channel; the server keeps
  the session and its subscriptions until their timeouts, and the next connection takes them over
  with TransferSubscriptions.
- TransferSubscriptions for anonymous sessions: open62541's default access control allows it only
  when both sessions use a secure channel (not SecurityPolicy None) and come from the same
  application, as Part 4 5.13.7 asks. Over None, anonymous subscriptions are recreated instead.
- Monitored item queues: the default server configuration caps every queue at 100 values
  (`queueSizeLimits`). `OpcUaServerOptions.MaxMonitoredItemQueueSize` (ABI 3) raises the cap.

## Subscriptions in the managed layer

The client does not use open62541's subscription API. It sends CreateSubscription, Publish,
Republish and the rest through the generic service call like any other request, and runs the
publish logic (acknowledgements, sequence gaps, keep-alive watchdog, transfer or recreate) in C#
(`SubscriptionEngine`). That keeps every protocol decision testable in C#, and lets a subscription
outlive the native client it was created on. A Publish request carries its own timeout hint
(several keep-alive periods), which is also the local timeout open62541 enforces for it.

## Known limits

- Linux binaries need glibc 2.34 or newer (Ubuntu 22.04+, Debian 12+, recent Raspberry Pi OS),
  because they are built on Ubuntu 24.04. Older edge devices need a build in an older container.
- ARM64 is cross-compiled and tested natively under qemu. The managed tests have not run on real
  ARM64 hardware yet.
