# RustProxy

RustProxy packages Cargo and rustup source files into portable `.rpk` archives, then provides a standalone Blazor web server for offline package management.

## Projects

- `RustProxy.Core`: package format, checksum validation, import and download services.
- `RustProxy.Cli`: package creation, explicit remote downloads and command-line imports.
- `RustProxy.Server`: independent ASP.NET Core Blazor Server application. It owns the web UI, package catalog, web upload flow and Cargo/rustup source endpoints.

All projects target `.NET 11`.

## Build and publish

```sh
dotnet build RustProxy.slnx
dotnet restore RustProxy.slnx --locked-mode
dotnet publish RustProxy.Server/RustProxy.Server.csproj \
  -c Release -r linux-x64 --self-contained false --no-restore \
  -o ./publish/server
```

The server is published as a separate executable. For a framework-dependent deployment, install the matching .NET 11 runtime on the offline machine. Use `--self-contained true` if the machine does not have the runtime installed.

### Automated release builds

The release workflow is provided in `.github/workflows/release.yml`, including conditional GitHub and Gitea release upload steps. A separate `.gitea/workflows/release.yml` is not currently tracked; configure workflow discovery for your Gitea instance before using it there. The GitHub workflow builds the server and CLI for `linux-x64`, `linux-arm64`, `win-x64` and `osx-arm64`, then creates a release and attaches one ZIP per platform when a `v*` tag is pushed. GitLab CI is provided in `.gitlab-ci.yml`; it verifies and packages the same four platforms on branch pushes and `v*` tags. Branch artifacts include the branch slug and short commit SHA in their names. A `v*` tag also creates a release after all packages succeed. GitHub and GitLab releases use `CHANGELOG.md` as their description. GitLab package artifacts expire after 30 days. You can also run the GitHub build manually with `workflow_dispatch`; manual runs upload downloadable workflow artifacts without creating a release.

Gitea repositories must define a repository secret named `GITEA_TOKEN` with permission to create releases and upload assets. Make sure the Gitea runner labels in the workflow (`ubuntu-latest`, `ubuntu-24.04-arm`, `windows-latest`, `macos-latest`) match runners available to your instance. GitHub's hosted `ubuntu-24.04-arm` runner may need to be replaced with a self-hosted runner label if unavailable on your GitHub plan.

The projects target `net11.0` and use C# 15. `global.json` selects .NET SDK `11.0.100-rc.3.26522.148`; GitHub Actions installs this SDK and GitLab CI uses the matching `11.0.100-rc.3` container image. Older .NET 11 previews may not recognize the C# 15 language version.

Start the published server:

```sh
ASPNETCORE_URLS=http://0.0.0.0:8080 ./publish/server/RustProxy.Server
```

Imported source files are stored as verified objects under `data/objects` relative to the server content root. Package metadata is stored in `data/metadata/rustproxy.db`. The web interface is available at `/`; health is available at `/health`.

### Administrator access

Set `Administration__ApiKey` to a randomly generated secret of at least 32 characters before enabling imports. The `/import` page prompts for HTTP Basic credentials: username `admin`, password equal to that secret. Leave the setting unset to disable administrator access. Remote administration requires HTTPS; HTTP credentials are accepted only on loopback connections. Configure TLS at the server or a trusted local reverse proxy.

`PublicBaseUrl` can specify the externally visible HTTP(S) base URL when a reverse proxy changes the origin or path prefix. The generated Cargo download URLs use this setting; otherwise they use the request origin.

Upload and extraction quotas are configurable in the `PackageLimits` configuration section (`MaxPackageBytes`, `MaxFileBytes`, `MaxExpandedBytes`, `MaxManifestBytes`, `MaxFiles`). Defaults allow a 4 GiB archive, a 4 GiB file, 32 GiB total expanded data, a 16 MiB manifest and 100,000 files. The service allows two active imports and applies a 30-minute deadline.

### Existing data

On first startup after an upgrade, the server makes a consistent SQLite backup from `data/rustproxy.db` into `data/metadata/rustproxy.db`, retaining the original database. Legacy `data/cargo` and `data/rustup` files with matching stored checksums are copied into object storage. Run one server instance per data directory during migration and retain a deployment backup. Original bytes that were already overwritten cannot be recovered from metadata alone; reimport the original package when needed.

## Offline web resources

The server UI does not use Google Fonts, Bootstrap CDN, JavaScript CDN, or any other external web resource. Its styles are in `RustProxy.Server/wwwroot/app.css`, and the Blazor runtime is included by the ASP.NET Core framework. The published `wwwroot` directory can therefore be copied into an isolated network without internet access.

## Package layout

The source directory is intentionally close to the files expected by the clients:

```text
source/
  cargo/
    config.json
    index/          # Cargo sparse index files
    crates/         # .crate archives
  rustup/
    dist/           # channel manifests and component archives
    static/         # rustup static metadata/files
```

The package contains the same tree plus `manifest.json`. The manifest records every file's size and SHA-256 checksum.

## CLI commands

Create a package from an existing source tree:

```sh
dotnet run --project RustProxy.Cli -- pack ./source ./rust.rpk rust-source https://static.rust-lang.org/
```

Download selected files from a source and package them:

```sh
dotnet run --project RustProxy.Cli -- download https://static.rust-lang.org/ ./rustup.rpk rustup dist/channel-rust-stable.toml
```

Use one configuration file to describe the Cargo project and rustup toolchains. Start from `examples/config.json`:

```json
{
  "name": "offline-rust-linux",
  "output": "artifacts/offline-rust-linux.rpk",
  "cargoIndexUrl": "https://index.crates.io/",
  "cargoDownloadUrl": "https://static.crates.io/crates/{crate}/{crate}-{version}.crate",
  "cargoProject": "./my-rust-project",
  "toolchains": [
    {
      "version": "1.88.0",
      "target": "x86_64-unknown-linux-gnu"
    }
  ],
  "retryCount": 5,
  "retryDelaySeconds": 3
}
```

Run the fetch:

```sh
dotnet run --project RustProxy.Cli -- fetch ./rustproxy.fetch.json
```

Paths in `output` and `cargoProject` are resolved relative to the configuration file. `version` can be `stable`, `beta`, `nightly`, an exact version such as `1.88.0`, or a dated channel such as `nightly-2026-01-01`. Exact versions are recommended for reproducible offline environments. Each fetch prints timestamps and stage progress. Transient network failures, timeouts, HTTP 5xx, 408 and 429 responses are retried with jittered exponential backoff capped at five minutes per delay; `Retry-After` is honored within that cap. Other HTTP 4xx and validation errors fail immediately.

The Cargo section uses exact registry dependencies from `Cargo.lock` when present. Otherwise RustProxy runs `cargo generate-lockfile` against the actual project directory, using Cargo on the system `PATH` or the cached standalone executable under `~/.rustproxy/cargo`. Keep workspace members, path dependencies and configured targets available when generating a lockfile, and use the workspace root for workspace fetches. This creates `Cargo.lock` without rewriting `Cargo.toml`. Git and local path dependencies are not included in the mirror; a lockfile containing multiple registry identities requires separate fetch packages. RustProxy downloads each unique sparse index entry once and fetches verified crate archives with bounded concurrency (`maxConcurrentDownloads`, default 4). The package preserves the source layout:

```text
cargo/
  index/
    config.json
    3/a/abc...
  crates/
    abc/
      abc-1.2.3.crate
```

`cargoIndexUrl` selects the sparse index. `cargoDownloadUrl` is optional; when omitted, the `dl` template from the upstream `config.json` is used. RustProxy follows that upstream template to download each archive, then stores it at the standard `cargo/crates/{crate}/{crate}-{version}.crate` path and writes a local `config.json` that points to that normalized layout. This also accepts mirrors that use a different download path, such as `/crates/{crate}/{version}/download`. Cargo.lock checksums are verified after each archive download.

This is a shared registry source package and can be used by multiple projects. Configure Cargo with the server's sparse URL:

```toml
[source.crates-io]
replace-with = "rustproxy"

[source.rustproxy]
registry = "sparse+http://host:8080/cargo/index/"
```

The rustup section downloads the selected channel manifest and all available files for the requested target. No component list is required: every matching component in the manifest is included automatically. Import the resulting package into the server, then point the offline client at `/rustup` using `RUSTUP_DIST_SERVER`.

The final `.rpk` is a ZIP package. Text metadata uses fast compression; already-compressed `.crate`, `.xz`, `.gz` and `.zip` resources are stored without recompression. Create the output archive outside the source tree; `manifest.json` is reserved by the package format.
The web server provides the same import workflow at `/import`, package browsing at `/`, and client configuration snippets at `/client-config`.

The HTTP endpoints live in `RustProxy.Server/Controllers`:

- `HealthController`: `/health`
- `CargoController`: `/cargo/{path}`
- `RustupController`: `/rustup/{path}`
- `PackageFilesController`: `/packages/{id:long}/{path}`

The CLI only creates, downloads and imports packages. Server hosting is owned by `RustProxy.Server`.

## Storage and caching

SQLite stores package, dependency, toolchain and file metadata under `data/metadata/rustproxy.db`. Verified resource bytes are stored once by SHA-256 under `data/objects/`. Each package references its original immutable objects; the shared Cargo and rustup routes resolve the most recently imported resource for each path.

- The home page caches a bounded metadata summary for 30 seconds and invalidates it after imports.
- File lookup uses indexed database queries and does not cache missing paths.
- Shared source responses use `Cache-Control: no-cache` and content-based ETags, with conditional requests and range processing.
- Package member responses use long-lived immutable caching; manifests and generated Cargo configuration use revalidation.
- A background integrity check verifies stored objects while preserving package metadata. `/health` reports liveness and `/health/ready` reports readiness.

`download` accepts explicit relative paths. A production mirror should add source-specific discovery and incremental synchronization for the Cargo index and rustup channel manifests, plus optional package signing before distributing packages.

## License

RustProxy is licensed under the [MIT License](LICENSE).
