# Changelog

## v0.1.1

### Changes

- Pin .NET SDK `11.0.100-rc.3.26522.148` across local builds, GitHub Actions, and GitLab CI to support C# 15 consistently.
- Build `linux-x64`, `linux-arm64`, `win-x64`, and `osx-arm64` packages on GitLab branch pipelines as well as version tags.
- Include the branch name and commit SHA in branch package names.
- Use `CHANGELOG.md` for release descriptions and add the MIT license.

### Downloads

Each platform package contains the server and CLI. Install the matching .NET 11 RC3 runtime on the destination machine; these packages are framework-dependent.

## v0.1.0

Initial release of RustProxy, an offline package builder and mirror server for Cargo and rustup sources.

### Highlights

- Package Cargo sparse indexes, crate archives, rustup manifests, and toolchain components into portable `.rpk` archives.
- Normalize mirror download layouts into the standard Cargo cache structure.
- Support JSON, TOML, and `Cargo.toml` fetch configuration sources from the CLI.
- Validate package paths and SHA-256 checksums during import.
- Serve imported Cargo and rustup sources through the ASP.NET Core server with package metadata and health endpoints.
- Add retry handling, cancellation support, and rollback for failed imports.
- Add GitHub Actions and GitLab CI release builds for `linux-x64`, `linux-arm64`, `win-x64`, and `osx-arm64`.

### Downloads

Each platform package contains the published server and CLI:

- `linux-x64`
- `linux-arm64`
- `win-x64`
- `osx-arm64`

The packages target the .NET 11 preview runtime and are framework-dependent.
