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
dotnet publish RustProxy.Server/RustProxy.Server.csproj \
  -c Release -r linux-x64 --self-contained false \
  -o ./publish/server
```

The server is published as a separate executable. For a framework-dependent deployment, install the matching .NET 11 runtime on the offline machine. Use `--self-contained true` if the machine does not have the runtime installed.

### Automated release builds

GitHub Actions and Gitea Actions workflows are provided in `.github/workflows/release.yml` and `.gitea/workflows/release.yml`. Both build the server and CLI for `linux-x64`, `linux-arm64`, `win-x64` and `osx-arm64`, then create a release and attach one ZIP per platform when a `v*` tag is pushed. You can also run the build manually with `workflow_dispatch`; manual runs upload downloadable workflow artifacts without creating a release.

Gitea repositories must define a repository secret named `GITEA_TOKEN` with permission to create releases and upload assets. Make sure the Gitea runner labels in the workflow (`ubuntu-latest`, `ubuntu-24.04-arm`, `windows-latest`, `macos-latest`) match runners available to your instance. GitHub's hosted `ubuntu-24.04-arm` runner may need to be replaced with a self-hosted runner label if unavailable on your GitHub plan.

The workflows use the .NET 11 preview SDK because the projects currently target `net11.0`.

Start the published server:

```sh
ASPNETCORE_URLS=http://0.0.0.0:8080 ./publish/server/RustProxy.Server
```

Imported packages are stored below `data/packages` relative to the server content root. The web interface is available at `/`; health is available at `/health`.

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

Use one configuration file to describe the Cargo project and rustup toolchains. Start from `rustproxy.fetch.example.json`:

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

Paths in `output` and `cargoProject` are resolved relative to the configuration file. `version` can be `stable`, `beta`, `nightly`, an exact version such as `1.88.0`, or a dated channel. Exact versions are recommended for reproducible offline environments. Each fetch prints timestamps and stage progress. Retryable network and I/O failures use exponential backoff: with the example above, attempts wait 3, 6, 12 and 24 seconds before the next attempt. HTTP 4xx and validation errors fail immediately.

The Cargo section requires only a directory containing `Cargo.toml`; a complete Rust project and source files are not required. If `Cargo.lock` exists, its exact registry dependencies are used. Otherwise RustProxy copies `Cargo.toml` (and an optional local `.cargo/config.toml`) to a temporary workspace, then uses `cargo` found on the system `PATH` or the cached standalone Cargo under `~/.rustproxy/cargo`. Cargo generates the lockfile in that temporary workspace, and RustProxy moves it back next to the original `Cargo.toml` without modifying the input manifest. RustProxy subsequently downloads the lockfile's sparse index entries and crate archives into the package. The package preserves the source layout:

```text
cargo/
  index/
    config.json
    3/a/abc...
  crates/
    abc/
      abc-1.2.3.crate
```

`cargoIndexUrl` selects the sparse index. `cargoDownloadUrl` is optional; when omitted, the `dl` template from the upstream `config.json` is used. The generated package `config.json` rewrites that template to `/cargo/...`, so offline Cargo requests stay inside RustProxy while the relative directory structure remains the same as the upstream source. Cargo.lock checksums are verified after each archive download.

This is a shared registry source package and can be used by multiple projects. Configure Cargo with the server's sparse URL:

```toml
[source.crates-io]
replace-with = "rustproxy"

[source.rustproxy]
registry = "sparse+http://host:8080/cargo/index/"
```

The rustup section downloads the selected channel manifest and all available files for the requested target. No component list is required: every matching component in the manifest is included automatically. Import the resulting package into the server, then point the offline client at `/rustup` using `RUSTUP_DIST_SERVER`.

The final `.rpk` is a ZIP package created with `CompressionLevel.Optimal`. Cargo source files and TOML compress well; already-compressed rustup archives (`.xz`, `.gz`, `.zip`) remain mostly unchanged, so the final package avoids nested `.rpk` files and compresses the directory tree only once.
The web server provides the same import workflow at `/import`, package browsing at `/`, and client configuration snippets at `/client-config`.

The HTTP endpoints live in `RustProxy.Server/Controllers`:

- `HealthController`: `/health`
- `CargoController`: `/cargo/{path}`
- `RustupController`: `/rustup/{path}`
- `PackageFilesController`: `/packages/{name}/{path}`

The CLI only creates, downloads and imports packages. Server hosting is owned by `RustProxy.Server`.

## Caching design

The source files are immutable after package import, so the server uses a two-level approach:

- `IMemoryCache` stores manifest lists and resolved file paths for 30 seconds to 5 minutes.
- The operating system file cache serves the large `.crate`, `.tar.xz` and component files without duplicating them in application memory.
- HTTP responses include `Cache-Control: public,max-age=31536000,immutable`, range processing and an ETag based on file length and modification time.
- Web imports invalidate the affected manifest and path cache entries immediately.

SQLite is intentionally not used as a binary cache. It becomes useful later for a large package catalog, package version queries, audit history, incremental synchronization state and full-text metadata search. If those requirements appear, SQLite should store metadata while package bytes remain on the filesystem.



`download` accepts explicit relative paths. A production mirror should add source-specific discovery and incremental synchronization for the Cargo index and rustup channel manifests, plus optional package signing before distributing packages.
