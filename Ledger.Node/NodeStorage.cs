using System.Text.Json;
using System.Text.Json.Serialization;
using Skyframe.Ledger.Core.Consensus;
using Skyframe.Ledger.Core.Models;
using Skyframe.Ledger.Core.Security;

namespace Skyframe.Ledger.Node;

public sealed class NodeStorage
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly NodeOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private NodeDiskData _data = new();
    public NodeStorage(NodeOptions options) => _options = options;
    public string WalletConfigPath => Path.Combine(_options.DataDirectory, "wallet_config.json");
    public string IdentityPath => Path.Combine(_options.DataDirectory, "node_identity.json");
    public bool HasWalletIdentity => File.Exists(IdentityPath) || File.Exists(WalletConfigPath);
    public string LedgerPath => Path.Combine(_options.DataDirectory, "ledger.json");
    public string PeersPath => Path.Combine(_options.DataDirectory, "peers.json");
    public string LogPath => Path.Combine(_options.DataDirectory, "node.log");
    public async Task InitializeAsync(CancellationToken token)
    {
        Directory.CreateDirectory(_options.DataDirectory);
        if (File.Exists(LedgerPath))
        {
            try { await using var stream=File.OpenRead(LedgerPath); _data = await JsonSerializer.DeserializeAsync<NodeDiskData>(stream, JsonOptions, token) ?? new NodeDiskData(); }
            catch (JsonException) { _data = new NodeDiskData(); }
        }
        var existingPeers = File.Exists(PeersPath) ? await LoadPeersAsync(token) : [];
        if (_options.Seed is not null) existingPeers.Insert(0, _options.Seed);
        else if (existingPeers.Count == 0) existingPeers.AddRange(await LoadBootstrapP2pSeedsAsync(token));
        await SavePeersAsync(existingPeers, token);
        if (!File.Exists(LedgerPath)) await SaveLedgerAsync(new LedgerState(), [], token);
    }
    private static async Task<List<string>> LoadBootstrapP2pSeedsAsync(CancellationToken token)
    {
        try
        {
            var root = AppContext.BaseDirectory;
            for (var dir = new DirectoryInfo(Environment.CurrentDirectory); dir is not null; dir = dir.Parent)
                if (File.Exists(Path.Combine(dir.FullName, "seeds.json"))) { root = dir.FullName; break; }
            var path = Path.Combine(root, "seeds.json");
            using var json = File.Exists(path)
                ? JsonDocument.Parse(await File.ReadAllTextAsync(path, token))
                : await JsonDocument.ParseAsync(await new HttpClient().GetStreamAsync(Skyframe.Ledger.Core.NetworkConstants.BootstrapSeedsUrl, token), cancellationToken: token);
            return json.RootElement.TryGetProperty("p2pSeeds", out var seeds) ? seeds.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrWhiteSpace(x)).Cast<string>().ToList() : [];
        }
        catch { return []; }
    }
    public async Task<(LedgerState State, List<Block> Blocks)> LoadLedgerAsync(CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            var state = new LedgerState { TotalSupplyAtomic = _data.TotalSupplyAtomic };
            foreach (var p in _data.Accounts) state.Accounts[p.Key] = p.Value;
            foreach (var p in _data.Escrows) state.Escrows[p.Key] = p.Value;
            return (state, _data.Blocks.ToList());
        }
        finally { _gate.Release(); }
    }
    public async Task SaveLedgerAsync(LedgerState state, IReadOnlyList<Block> blocks, CancellationToken token)
    {
        await _gate.WaitAsync(token);
        try
        {
            _data = new NodeDiskData { Blocks = blocks.ToList(), Accounts = new(state.Accounts), Escrows = new(state.Escrows), TotalSupplyAtomic = state.TotalSupplyAtomic };
            await WriteJsonAtomicallyAsync(LedgerPath, _data, token);
        }
        finally { _gate.Release(); }
    }
    public async Task<List<string>> LoadPeersAsync(CancellationToken token)
    {
        if (!File.Exists(PeersPath)) return [];
        var stored = await File.ReadAllTextAsync(PeersPath, token);
        if (stored.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith('['))
        {
            var legacyPeers = JsonSerializer.Deserialize<List<string>>(stored, JsonOptions) ?? [];
            await SavePeersAsync(legacyPeers, token);
            return legacyPeers;
        }
        var plaintext = MachineBoundDataProtection.DecryptPeersJson(_options.DataDirectory, stored);
        return JsonSerializer.Deserialize<List<string>>(plaintext, JsonOptions) ?? [];
    }

    public Task SavePeersAsync(IEnumerable<string> peers, CancellationToken token)
    {
        var list = peers.Distinct(StringComparer.OrdinalIgnoreCase).Order().ToList();
        var json = JsonSerializer.Serialize(list, JsonOptions);
        var encrypted = MachineBoundDataProtection.EncryptPeersJson(_options.DataDirectory, json);
        return WriteTextAtomicallyAsync(PeersPath, encrypted, token);
    }
    public async Task<JsonDocument?> LoadWalletConfigAsync(CancellationToken token)
    {
        if (File.Exists(IdentityPath))
        {
            var decrypted = MachineBoundDataProtection.LoadIdentity(_options.DataDirectory);
            return JsonDocument.Parse(decrypted);
        }
        if (!File.Exists(WalletConfigPath)) return null;
        var legacyPlaintext = await File.ReadAllTextAsync(WalletConfigPath, token);
        using var validation = JsonDocument.Parse(legacyPlaintext);
        MachineBoundDataProtection.SaveIdentity(_options.DataDirectory, legacyPlaintext);
        File.Delete(WalletConfigPath);
        return JsonDocument.Parse(legacyPlaintext);
    }

    private static async Task WriteTextAtomicallyAsync(string path, string contents, CancellationToken token)
    {
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, contents, new System.Text.UTF8Encoding(false), token);
        File.Move(temp, path, true);
    }
    private static async Task WriteJsonAtomicallyAsync<T>(string path, T value, CancellationToken token)
    {
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp)) await JsonSerializer.SerializeAsync(stream, value, JsonOptions, token);
        File.Move(temp, path, true);
    }
    private sealed class NodeDiskData
    {
        public NodeDiskData() { }
        public List<Block> Blocks { get; set; } = [];
        public Dictionary<string, AccountState> Accounts { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, EscrowRecord> Escrows { get; set; } = new(StringComparer.Ordinal);
        public long TotalSupplyAtomic { get; set; }
    }
}


