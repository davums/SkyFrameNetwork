using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skyframe.Ledger.Core;
using Skyframe.Ledger.Core.Models;
using Microsoft.Maui.Storage;

namespace Ledger.Mobile;

public sealed record AccountSnapshot(long BalanceAtomic, long Nonce, int MatchingNodes, int CheckedNodes);
public sealed record QuorumResult(bool Success, string Message, AccountSnapshot? Snapshot = null, string? LocalNodeWalletAddress = null);
public sealed record BroadcastResult(bool Accepted, string Message, int MempoolNodes, string TxId);
public sealed record PaymentObservation(string Status, string? TxId, int MatchingNodes, int CheckedNodes);

/// <summary>Uses a dynamically sized HTTPS quorum, with loopback-only single-node mode for Windows desktop development.</summary>
public sealed class NodeQuorumClient
{
    private const string NodesKey = "skyframe.public-node-gateways";
    private const int MaximumQuorumSize = 5;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };

    public string GetSavedNodes()
    {
        var nodes = Preferences.Default.Get(NodesKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(nodes)) return nodes;
        nodes = Preferences.Default.Get("kora.public-node-gateways", string.Empty);
        if (!string.IsNullOrWhiteSpace(nodes))
        {
            Preferences.Default.Set(NodesKey, nodes);
            return nodes;
        }
        return string.Empty;
    }
    public void SaveNodes(string input)
    {
        var nodes = ParseNodes(input);
        Preferences.Default.Set(NodesKey, string.Join(Environment.NewLine, nodes));
    }

    public static string NormalizeEndpoint(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length == 0) throw new ArgumentException("Node endpoint is empty.");
        if (!candidate.Contains("://", StringComparison.Ordinal)) candidate = "https://" + candidate;
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            throw new ArgumentException($"'{value}' must be a valid HTTPS node gateway URL.");
        var localDesktop = OperatingSystem.IsWindows() && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && uri.Port == 6001;
        if (uri.Scheme != Uri.UriSchemeHttps && !localDesktop)
            throw new ArgumentException("Only HTTPS gateways are allowed, except http://127.0.0.1:6001 for Windows desktop testing.");
        return uri.AbsoluteUri.EndsWith('/') ? uri.AbsoluteUri : uri.AbsoluteUri + '/';
    }

    private static string[] ParseNodes(string input) => input.Split(new[] { '\r', '\n', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(NormalizeEndpoint).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private Uri[] SelectNodes(out int requiredQuorum)
    {
        var configuredOrDiscoveredNodes = ParseNodes(GetNodesForUseAsync().GetAwaiter().GetResult()).Select(value => new Uri(value)).ToArray();
        if (configuredOrDiscoveredNodes.Length == 0)
            throw new InvalidOperationException("Configure at least one node gateway.");
        requiredQuorum = Math.Clamp(configuredOrDiscoveredNodes.Length, 1, MaximumQuorumSize);
        var endpoints = configuredOrDiscoveredNodes;
        var local = endpoints.Where(IsLocalDesktopEndpoint).ToArray();
        if (local.Length > 0)
        {
            if (endpoints.Length != 1) throw new InvalidOperationException("Use the local desktop endpoint by itself, or configure HTTPS node gateways.");
            return local;
        }
        var selected = endpoints.OrderBy(_ => Random.Shared.Next()).Take(MaximumQuorumSize).ToArray();
        return selected;
    }

    private static async Task<string> GetNodesForUseAsync()
    {
        var saved = new NodeQuorumClient().GetSavedNodes();
        if (!string.IsNullOrWhiteSpace(saved)) return saved;
        var seeds = await LoadMobileSeedsAsync();
        var discovered = new List<string>(seeds);
        foreach (var seed in seeds)
        {
            try
            {
                var gateways = await Http.GetFromJsonAsync<List<string>>(new Uri(new Uri(NormalizeEndpoint(seed)), "peers"));
                if (gateways is not null) discovered.AddRange(gateways);
            }
            catch { }
            if (discovered.Count >= MaximumQuorumSize) break;
        }
        var result = ParseNodes(string.Join(Environment.NewLine, discovered)).Take(MaximumQuorumSize).ToArray();
        return string.Join(Environment.NewLine, result);
    }

    private static async Task<List<string>> LoadMobileSeedsAsync()
    {
        try
        {
            using var stream = await FileSystem.OpenAppPackageFileAsync("seeds.json");
            using var json = await JsonDocument.ParseAsync(stream);
            return json.RootElement.GetProperty("mobileGateways").EnumerateArray().Select(e => e.GetString()).Where(v => !string.IsNullOrWhiteSpace(v)).Cast<string>().ToList();
        }
        catch
        {
            try
            {
                using var json = await JsonDocument.ParseAsync(await Http.GetStreamAsync(NetworkConstants.BootstrapSeedsUrl));
                return json.RootElement.GetProperty("mobileGateways").EnumerateArray().Select(e => e.GetString()).Where(v => !string.IsNullOrWhiteSpace(v)).Cast<string>().ToList();
            }
            catch { return []; }
        }
    }

    private static bool IsLocalDesktopEndpoint(Uri endpoint) => OperatingSystem.IsWindows()
        && endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback && endpoint.Port == 6001;

    public async Task<QuorumResult> GetBalanceAsync(string address, CancellationToken cancellationToken = default)
    {
        Uri[] selected;
        int requiredQuorum;
        try { selected = SelectNodes(out requiredQuorum); }
        catch (Exception ex) { return new(false, ex.Message); }

        var responses = await Task.WhenAll(selected.Select(async node =>
        {
            var statusUrl = new Uri(node, "status");
            var balanceUrl = new Uri(node, $"balance/{Uri.EscapeDataString(address)}");
            try
            {
                using var status = await Http.GetAsync(statusUrl, cancellationToken);
                var statusBody = await status.Content.ReadAsStringAsync(cancellationToken);
                if (!status.IsSuccessStatusCode)
                    return (Ok: false, Balance: 0L, Nonce: 0L, NodeWalletAddress: (string?)null, Detail: $"GET {statusUrl} returned {(int)status.StatusCode} {status.ReasonPhrase}: {statusBody}");
                using var statusJson = JsonDocument.Parse(statusBody);
                if (!MatchesGenesis(statusJson.RootElement))
                {
                    var actualGenesis = statusJson.RootElement.TryGetProperty("genesisHash", out var hash) ? hash.ToString() : "<missing>";
                    return (Ok: false, Balance: 0L, Nonce: 0L, NodeWalletAddress: (string?)null, Detail: $"GET {statusUrl} returned genesisHash {actualGenesis}, expected {NetworkConstants.OfficialGenesisHash}.");
                }
                var nodeWalletAddress = statusJson.RootElement.TryGetProperty("walletAddress", out var walletAddress) && walletAddress.ValueKind == JsonValueKind.String
                    ? walletAddress.GetString()
                    : null;
                using var balance = await Http.GetAsync(balanceUrl, cancellationToken);
                var balanceBody = await balance.Content.ReadAsStringAsync(cancellationToken);
                if (!balance.IsSuccessStatusCode)
                    return (Ok: false, Balance: 0L, Nonce: 0L, NodeWalletAddress: (string?)null, Detail: $"GET {balanceUrl} returned {(int)balance.StatusCode} {balance.ReasonPhrase}: {balanceBody}");
                using var json = JsonDocument.Parse(balanceBody);
                var root = json.RootElement;
                long sat = 0, next = 0;
                var hasBalance = root.TryGetProperty("balanceAtomic", out var amount) && amount.TryGetInt64(out sat);
                if (!hasBalance && root.TryGetProperty("balanceSkyf", out var skyf) && skyf.TryGetDecimal(out var skyfAmount))
                {
                    try { sat = decimal.ToInt64(decimal.Round(skyfAmount * 100_000_000m, 0, MidpointRounding.AwayFromZero)); hasBalance = true; }
                    catch (OverflowException) { hasBalance = false; }
                }
                var hasNonce = root.TryGetProperty("nextNonce", out var nonce) && nonce.TryGetInt64(out next);
                var detail = hasBalance && hasNonce ? string.Empty : $"GET {balanceUrl} JSON did not contain valid balanceAtomic/balanceSkyf and nextNonce fields: {balanceBody}";
                return (Ok: hasBalance && hasNonce, Balance: sat, Nonce: next, NodeWalletAddress: nodeWalletAddress, Detail: detail);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException)
            { return (Ok: false, Balance: 0L, Nonce: 0L, NodeWalletAddress: (string?)null, Detail: $"Request to {statusUrl} / {balanceUrl} failed: {ex.GetType().Name}: {ex.Message}"); }
        }));

        var votes = responses.Where(r => r.Ok).GroupBy(r => (r.Balance, r.Nonce)).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (votes is null || votes.Count() < requiredQuorum)
        {
            var failures = string.Join(" | ", responses.Where(r => !r.Ok && !string.IsNullOrWhiteSpace(r.Detail)).Select(r => r.Detail));
            var summary = $"Account consensus needs {requiredQuorum} matching node(s); {responses.Count(r => r.Ok)} of {selected.Length} node(s) returned matching account data.";
            return new(false, failures.Length == 0 ? summary : $"{summary} {failures}");
        }
        var localNodeWalletAddress = responses.FirstOrDefault(r => r.Ok).NodeWalletAddress;
        return new(true, $"Account consensus: {votes.Count()} of {selected.Length} node(s).", new(votes.Key.Balance, votes.Key.Nonce, votes.Count(), selected.Length), localNodeWalletAddress);
    }
    public async Task<BroadcastResult> BroadcastAsync(Transaction transaction, CancellationToken cancellationToken = default)
    {
        Uri[] selected;
        int requiredQuorum;
        try { selected = SelectNodes(out requiredQuorum); }
        catch (Exception ex) { return new(false, ex.Message, 0, transaction.TxId); }

        var verified = await GetOfficialNodesAsync(selected, cancellationToken);
        if (verified.Length < requiredQuorum) return new(false, $"Only {verified.Length} of {selected.Length} configured node(s) match the official genesis hash; {requiredQuorum} required.", 0, transaction.TxId);
        var accepted = await Task.WhenAll(verified.Select(async node =>
        {
            try
            {
                using var response = await Http.PostAsJsonAsync(new Uri(node, "tx"), transaction, cancellationToken);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
        }));
        await Task.Delay(350, cancellationToken);
        var seen = await Task.WhenAll(verified.Select(async node =>
        {
            try
            {
                using var response = await Http.GetAsync(new Uri(node, $"mempool/{Uri.EscapeDataString(transaction.TxId)}"), cancellationToken);
                if (!response.IsSuccessStatusCode) return false;
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return json.RootElement.TryGetProperty("pending", out var pending) && pending.GetBoolean();
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return false; }
        }));
        var confirmations = seen.Count(v => v);
        var submitted = accepted.Count(v => v);
        return new(confirmations >= requiredQuorum, confirmations >= requiredQuorum
            ? $"Mempool observed by {confirmations} official node(s)."
            : $"Submitted to {submitted} official node(s); only {confirmations} currently report it in their mempool.", confirmations, transaction.TxId);
    }

    public async Task<PaymentObservation> CheckIncomingPaymentAsync(string address, long amountAtomic, long afterTimestampUtc, string? reference = null, CancellationToken cancellationToken = default)
    {
        Uri[] selected;
        int requiredQuorum;
        try { selected = SelectNodes(out requiredQuorum); }
        catch { return new("unavailable", null, 0, 0); }
        var official = await GetOfficialNodesAsync(selected, cancellationToken);
        var results = await Task.WhenAll(official.Select(async node =>
        {
            try
            {
                var path = $"payments/lookup?address={Uri.EscapeDataString(address)}&amountAtomic={amountAtomic}&afterTimestampUtc={afterTimestampUtc}&reference={Uri.EscapeDataString(reference ?? "")}";
                using var response = await Http.GetAsync(new Uri(node, path), cancellationToken);
                if (!response.IsSuccessStatusCode) return (Status: "none", Id: (string?)null);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                var root = json.RootElement;
                return (Status: root.TryGetProperty("status", out var state) ? state.GetString() ?? "none" : "none",
                    Id: root.TryGetProperty("txId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (Status: "none", Id: (string?)null); }
        }));
        var confirmed = results.Where(x => x.Status == "confirmed" && x.Id is not null).GroupBy(x => x.Id).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (confirmed?.Count() >= requiredQuorum) return new("confirmed", confirmed.Key, confirmed.Count(), official.Length);
        var pending = results.Where(x => x.Status == "mempool" && x.Id is not null).GroupBy(x => x.Id).OrderByDescending(g => g.Count()).FirstOrDefault();
        if (pending?.Count() >= requiredQuorum) return new("mempool", pending.Key, pending.Count(), official.Length);
        return new("none", null, results.Count(x => x.Status is "mempool" or "confirmed"), official.Length);
    }

    public async Task<IReadOnlyList<EscrowRecord>> GetEscrowsAsync(CancellationToken cancellationToken = default)
    {
        Uri[] selected;
        int requiredQuorum;
        try { selected = SelectNodes(out requiredQuorum); }
        catch { return Array.Empty<EscrowRecord>(); }
        var official = await GetOfficialNodesAsync(selected, cancellationToken);
        var replies = await Task.WhenAll(official.Select(async node =>
        {
            try
            {
                using var response = await Http.GetAsync(new Uri(node, "escrows"), cancellationToken);
                if (!response.IsSuccessStatusCode) return new List<EscrowRecord>();
                return await response.Content.ReadFromJsonAsync<List<EscrowRecord>>(JsonOptions, cancellationToken) ?? [];
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return new List<EscrowRecord>(); }
        }));
        return replies.SelectMany(list => list)
            .GroupBy(e => e.EscrowId, StringComparer.Ordinal)
            .Select(group => group.GroupBy(e => (e.Status, e.IsDisputed, e.ExpiryTimestampUtc)).OrderByDescending(vote => vote.Count()).First())
            .Where(vote => vote.Count() >= requiredQuorum)
            .Select(vote => vote.First())
            .ToArray();
    }

    private static async Task<Uri[]> GetOfficialNodesAsync(IEnumerable<Uri> nodes, CancellationToken token)
    {
        var checks = await Task.WhenAll(nodes.Select(async node =>
        {
            try
            {
                using var response = await Http.GetAsync(new Uri(node, "status"), token);
                if (!response.IsSuccessStatusCode) return (Node: node, Official: false);
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token));
                return (Node: node, Official: MatchesGenesis(json.RootElement));
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { return (Node: node, Official: false); }
        }));
        return checks.Where(c => c.Official).Select(c => c.Node).ToArray();
    }

    private static bool MatchesGenesis(JsonElement root) => root.TryGetProperty("genesisHash", out var genesis)
        && string.Equals(genesis.GetString(), NetworkConstants.OfficialGenesisHash, StringComparison.OrdinalIgnoreCase);
}
