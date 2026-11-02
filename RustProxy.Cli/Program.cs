using System.Text.Json;
using RustProxy.Core.Download;
using RustProxy.Core.Package;
using RustProxy.Core.Serialization;
using RustProxy.Core.Storage;
using RustProxy.Core.Sync;
using RustProxy.Core.Tools;

var cancellationToken = CancellationToken.None;
if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

try
{
    var store = new PackageStore();
    switch (args[0].ToLowerInvariant())
    {
        case "pack":
            if (args.Length < 4) throw new ArgumentException("Usage: pack <source-directory> <output.rpk> <name> [source-url]");
            await store.CreateFromDirectoryAsync(args[1], args[2], args[3], args.Length > 4 ? args[4] : null, cancellationToken);
            Console.WriteLine($"Created {args[2]}");
            return 0;
        case "import":
            if (args.Length != 3) throw new ArgumentException("Usage: import <package.rpk> <destination-directory>");
            var manifest = await store.ImportAsync(new ImportOptions { PackagePath = args[1], DestinationDirectory = args[2] }, cancellationToken);
            Console.WriteLine($"Imported {manifest.Name} ({manifest.Files.Count} files)");
            return 0;
        case "fetch":
            if (args.Length != 2) throw new ArgumentException("Usage: fetch <rustproxy.fetch.json>");
            var configPath = Path.GetFullPath(args[1]);
            var config = JsonSerializer.Deserialize(await File.ReadAllTextAsync(configPath, cancellationToken), RustProxyJsonContext.Default.FetchConfiguration) ?? throw new InvalidDataException("Invalid fetch configuration.");
            var configDirectory = Path.GetDirectoryName(configPath)!;
            if (!Path.IsPathRooted(config.Output)) config.Output = Path.Combine(configDirectory, config.Output);
            if (!string.IsNullOrWhiteSpace(config.CargoProject) && !Path.IsPathRooted(config.CargoProject)) config.CargoProject = Path.Combine(configDirectory, config.CargoProject);
            using (var client = new SourceHttpClient())
            {
                var logger = new ConsoleFetchLogger();
                var retry = new RetryExecutor(logger);
                var coordinator = new FetchCoordinator(store, new CargoDependencyFetcher(client, retry, logger), new RustupToolchainFetcher(client, retry, logger), logger);
                await coordinator.FetchAsync(config, cancellationToken);
            }
            return 0;
        case "download":
            if (args.Length < 5) throw new ArgumentException("Usage: download <source-url> <output.rpk> <name> <relative-path> [relative-path ...]");
            using (var client = new SourceHttpClient())
            {
                var downloader = new SourceDownloader(client, store);
                await downloader.DownloadAsync(new SourceDownloadOptions { Source = new Uri(args[1]), OutputPath = args[2], Name = args[3], Paths = args[4..] }, cancellationToken);
            }
            Console.WriteLine($"Created {args[2]}");
            return 0;
        default:
            throw new ArgumentException($"Unknown command: {args[0]}");
    }
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidDataException or UriFormatException)
{
    Console.Error.WriteLine($"Error: {exception.Message}");
    return 2;
}

static void PrintUsage() => Console.WriteLine("RustProxy\n\nCommands:\n  fetch <rustproxy.fetch.json>\n  pack <source-dir> <output.rpk> <name> [source-url]\n  download <source-url> <output.rpk> <name> <path> [path ...]\n  rustup-fetch <toolchain> <target> <output.rpk> <name>\n  import <package.rpk> <destination-dir>\n\nExpected source layout:\n  cargo/config.json, cargo/index/**, cargo/crates/**\n  rustup/dist/**, rustup/static/**");
