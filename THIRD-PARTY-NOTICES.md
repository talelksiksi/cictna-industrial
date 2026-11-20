# Third-party notices

The packages built from this repository include the following third-party components.
Full licence texts are in `native/licenses/` in the source tree and in `licenses/` inside
the NuGet packages.

## open62541 v1.5.10

- Licence: Mozilla Public License 2.0 (`licenses/open62541-MPL-2.0.txt`).
- Source: https://github.com/open62541/open62541/tree/v1.5.10 (commit 93c3bdfc9ea4718b503c6cbf57876237addd0f2c).
- Used unmodified. It is statically linked into `cictna_ua`, which MPL-2.0 allows for
  combination with code under other licences. Any change made to open62541's own files
  must be published under MPL-2.0 when distributed; this repository makes none.
- Bundled dependencies compiled in, each under its own permissive licence
  (headers reproduced in `licenses/open62541-bundled-deps.txt`):
  base64 (BSD), cj5 (MIT), dtoa (Boost Software License 1.0), itoa (MIT), libc_time (MIT, from musl),
  mp_printf (MIT), musl_inet_pton (MIT), open62541_queue (BSD-3-Clause), parse_num (MIT, from musl),
  pcg_basic (Apache-2.0), utf8 (MPL-2.0), yxml (MIT), ziptree (MPL-2.0).
- Not compiled in with our build options: mdnsd, mqtt-c, nodesetLoader, ua-nodeset.

## Mbed TLS v3.6.7

- Licence: dual Apache-2.0 OR GPL-2.0-or-later. **We use it under Apache-2.0.**
  (`licenses/mbedtls-LICENSE.txt`)
- Source: https://github.com/Mbed-TLS/mbedtls/tree/v3.6.7 (commit 068ff080b369adfac81509f9b57b2afabaf82dc5).
- Used unmodified, statically linked into `cictna_ua`.

## Microsoft.Extensions.Logging.Abstractions 10.0.x

- Licence: MIT. NuGet dependency of Cictna.Industrial.OpcUa (installed from nuget.org, not bundled).

## OPC UA StatusCode table

- `src/Cictna.Industrial.Core/StatusCodes.g.cs` is generated from StatusCode.csv by the OPC
  Foundation (MIT licence), as shipped with open62541 in tools/schema.

## Build and test tools (not redistributed)

- .NET SDK and runtime: MIT.
- xUnit.net v3: Apache-2.0.
- node-opcua (MIT) and its dependencies: interop test server only (tests/Interop), never shipped.
- modbus-serial (ISC) and jsmodbus (MIT) with their dependencies: Modbus interop test peers only (tests/Interop/modbus-peers), never shipped.

When a native dependency version changes in `native/versions.env`, rebuild, run
`python3 scripts/collect-native-licenses.py` and update this file.

This file is a good-faith summary, not legal advice. Have it reviewed before selling closed products.
