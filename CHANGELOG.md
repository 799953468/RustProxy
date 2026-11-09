# Changelog

## v0.1.3

### Changes

- Include the security, storage recovery, Cargo/rustup synchronization, and CI improvements described in v0.1.2 below.
- Normalize test workspace paths with the operating system's path rules, fixing Windows failures in the legacy database migration and relative TOML configuration tests.
- Remove the unsupported `include-prerelease` input from GitHub SDK setup; `global.json` continues to select the pinned .NET 11 RC3 SDK.

### Release and upgrade notes

- GitHub publication of v0.1.2 was blocked by the Windows test fixture path assertions; v0.1.3 fixes that release gate without replacing the existing tag.
- The v0.1.2 administrator configuration, data migration, four-platform downloads, and .NET 11 RC3 runtime requirements also apply to this version.

## v0.1.2

### Security

- Protect server imports with administrator authentication, require HTTPS for remote credentials, and reject revoked credentials during an upload.
- Harden package validation against path traversal, symbolic links, duplicate entries, malformed ZIP/ZIP64 directories, and inconsistent manifests or checksums.
- Enforce configurable limits on compressed packages, expanded files, manifests, ZIP directories, and file counts, with bounded concurrent imports and import deadlines.

### Storage and reliability

- Store verified files as immutable SHA-256 objects so overlapping imports preserve each package's original contents and avoid duplicating identical bytes.
- Add rollback and recovery for interrupted imports, migrate legacy SQLite metadata and verified files, and prevent multiple servers from sharing one data directory.
- Check stored object integrity in the background and report database, disk-space, and integrity readiness through `/health/ready`; retain `/health` for liveness.
- Use indexed file lookups and bounded metadata caching, support conditional and range requests, and apply immutable caching to package-scoped resources.

### Cargo and rustup

- Resolve Cargo workspace lockfiles correctly, verify crate checksums, reject conflicting registry metadata, and download crates with bounded concurrency.
- Normalize upstream mirror download templates into the standard Cargo layout and generate package-scoped registry configuration.
- Correct rustup channel, dated-version, component, and updater downloads; verify downloaded content and improve transient HTTP retry and cancellation handling.
- Tighten fetch configuration validation and clean up cancelled external Cargo commands.

### Build and CI

- Add locked NuGet restores and a regression suite with 73 passing tests covering package security, storage recovery, authentication, downloads, configuration, and HTTP endpoints.
- Add GitHub CI for branch commits and pull requests on Linux x64, Linux ARM64, Windows x64, and macOS ARM64; reuse the same verification before release packaging.
- Add GitLab commit, merge request, and tag verification, suppress duplicate branch pipelines for open merge requests, and retain TRX test reports for 14 days on both platforms.

### Upgrade and downloads

- Configure `Administration__ApiKey` with a secret of at least 32 characters to enable imports. Use the `admin` username with this secret and HTTPS for remote administration.
- Back up the deployment before upgrading. The first startup copies legacy metadata into `data/metadata/rustproxy.db` and migrates files whose stored checksums match into `data/objects`; retain the original data until migration is verified. Reimport original packages if legacy bytes were already overwritten.
- Run one server instance per data directory. Configure `PublicBaseUrl` when a reverse proxy changes the public origin or path prefix.
- Packages contain the server and CLI for `linux-x64`, `linux-arm64`, `win-x64`, and `osx-arm64`, with SHA-256 checksum files. Install the matching .NET 11 RC3 runtime; the packages remain framework-dependent.

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
