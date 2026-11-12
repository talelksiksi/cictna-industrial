# Coding standards

## C#
- .NET 10, nullable enabled, warnings as errors, `latest-recommended` analyzers. Style rules live in `.editorconfig`.
- Every public type and member has XML docs; non-trivial APIs get an `<example>`.
- Async all the way for I/O. Every network operation takes a `CancellationToken` and has a timeout.
  No `.Result` or `.Wait()` on tasks.
- Values read from devices always carry quality (OPC UA status code) and source/server timestamps.
- Errors on expected paths (device offline, bad quality, timeout) are values, not exceptions.
  Exceptions are for programming errors and broken installs.
- Logging through `Microsoft.Extensions.Logging` abstractions, structured, no string concatenation.
- Package versions only in `Directory.Packages.props`.

## Native interop
- The C shim (`native/shim/`) is the only native surface. It exposes plain C types, returns
  OPC UA status codes, and hides open62541 inline functions and macros.
- `LibraryImport` (source-generated), never `DllImport`.
- Every native object is owned by a `SafeHandle`. No `IntPtr` leaves the `*.Native` assembly.
- Native callbacks never throw into native code; they capture and marshal to managed code.
- open62541 and mbedTLS sources are never edited. Version bumps go through `native/versions.env`
  with the new commit hash, followed by a full rebuild, the licence collector and the notices file.

## Tests
- xUnit v3 on Microsoft Testing Platform. Test names describe behaviour in PascalCase.
- Unit tests must not depend on timing luck: use explicit timeouts and generous upper bounds.
- Interop, chaos and soak suites live in their own projects so the fast suite stays fast.

## Git
- Small commits with a clear subject line in the imperative. Push daily.
- History carries only the noreply identity.
