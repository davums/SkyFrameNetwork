using System.Globalization;

namespace Skyframe.Ledger.Node;

public sealed record NodeOptions(int Port, string DataDirectory, string? Seed, bool Testnet, bool PublicApi = false)
{
    public static NodeOptions Parse(string[] args)
    {
        var port = 5001; var dataDirectory = GetDefaultDataDirectory(); string? seed = null; var testnet = false; var publicApi = false;
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg == "--testnet") { testnet = true; continue; }
            if (arg == "--public-api") { publicApi = true; continue; }
            if (arg is "--port" or "--data-dir" or "--seed" or "-seed")
            {
                if (++i >= args.Length) throw new ArgumentException($"Missing value for {arg}.");
                var value = args[i];
                switch (arg)
                {
                    case "--port":
                        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 64535) throw new ArgumentException("--port must be between 1 and 64535.");
                        break;
                    case "--data-dir": dataDirectory = value; break;
                    case "--seed": case "-seed": seed = value; break;
                }
                continue;
            }
            throw new ArgumentException($"Unknown option: {arg}");
        }
        return new NodeOptions(port, Path.GetFullPath(dataDirectory), seed, testnet, publicApi);
    }

    private static string GetDefaultDataDirectory()
    {
        // Keep the dashboard and node on the same wallet/ledger directory when run from this solution.
        foreach (var start in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "SkyframeNetwork.sln"))) return Path.Combine(directory.FullName, "Skyframe", "data");
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Skyframe", "data");
    }
}
