using System.Text.Json;
using System.Text.Json.Serialization;
using Skyframe.Ledger.Core;
using Skyframe.Ledger.Core.Models;
using Skyframe.Ledger.Node.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;

namespace Skyframe.Ledger.Node;

/// <summary>Optional public read and signed-transaction gateway. The local control API remains loopback-only.</summary>
public sealed class PublicNodeApiService : BackgroundService
{
    private static readonly JsonSerializerOptions TransactionJson = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly NodeOptions _options;
    private readonly NodeRuntime _runtime;
    private readonly NodeStorage _storage;
    private readonly PeerNetwork _peers;
    private readonly ILogger<PublicNodeApiService> _logger;
    private WebApplication? _app;

    public PublicNodeApiService(NodeOptions options, NodeRuntime runtime, NodeStorage storage, PeerNetwork peers, ILogger<PublicNodeApiService> logger)
    {
        _options = options;
        _runtime = runtime;
        _storage = storage;
        _peers = peers;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.PublicApi)
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
            return;
        }

        var builder = WebApplication.CreateSlimBuilder();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
                RateLimitPartition.GetFixedWindowLimiter(
                    context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    _ => new FixedWindowRateLimiterOptions { PermitLimit = 120, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
        builder.WebHost.UseUrls($"http://0.0.0.0:{_options.Port + 1001}");
        _app = builder.Build();
        _app.UseRateLimiter();
        _app.MapGet("/status", () => Results.Ok(new
        {
            genesisHash = NetworkConstants.OfficialGenesisHash,
            status = _runtime.IsRunning ? "running" : "standby",
            blockHeight = _runtime.BlockHeight,
            activePeers = _runtime.ActivePeers,
            mempoolCount = _runtime.MempoolCount,
            testnet = _options.Testnet
        }));
        _app.MapGet("/balance/{address}", async (string address, CancellationToken token) =>
        {
            var (state, _) = await _storage.LoadLedgerAsync(token);
            var found = state.Accounts.TryGetValue(address, out var account);
            return Results.Ok(new { address, balanceAtomic = found ? account!.BalanceAtomic : 0, nextNonce = found ? account!.NextNonce : 0 });
        });
        _app.MapGet("/escrows", async (CancellationToken token) =>
        {
            var (state, _) = await _storage.LoadLedgerAsync(token);
            return Results.Ok(state.Escrows.Values);
        });
        _app.MapGet("/mempool/{txId}", (string txId) =>
            Results.Ok(new { txId, pending = _runtime.FindPendingTransaction(t => string.Equals(t.TxId, txId, StringComparison.Ordinal)) is not null }));
        _app.MapGet("/payments/lookup", async (string address, long amountAtomic, long afterTimestampUtc, string? reference, CancellationToken token) =>
        {
            var pending = _runtime.FindPendingTransaction(t => t.Type == TransactionType.Transfer && t.ToAddress == address
                && t.AmountAtomic == amountAtomic && t.TimestampUtc >= afterTimestampUtc && HasReference(t.PayloadJson, reference));
            if (pending is not null) return Results.Ok(new { status = "mempool", txId = pending.TxId });
            var (_, blocks) = await _storage.LoadLedgerAsync(token);
            var confirmed = blocks.SelectMany(b => b.Transactions).FirstOrDefault(t => t.Type == TransactionType.Transfer
                && t.ToAddress == address && t.AmountAtomic == amountAtomic && t.TimestampUtc >= afterTimestampUtc && HasReference(t.PayloadJson, reference));
            return Results.Ok(new { status = confirmed is null ? "none" : "confirmed", txId = confirmed?.TxId });
        });
        _app.MapPost("/tx", async (HttpRequest request, CancellationToken token) =>
        {
            try
            {
                var transaction = await JsonSerializer.DeserializeAsync<Transaction>(request.Body, TransactionJson, token);
                if (transaction is null) return Results.BadRequest(new { error = "Transaction JSON is required." });
                var queued = _runtime.TryEnqueue(transaction);
                if (queued) await _peers.GossipAsync(new PeerMessage("NEW_TX", Transaction: transaction), token);
                return Results.Accepted(value: new { transaction.TxId, status = queued ? "queued" : "duplicate" });
            }
            catch (JsonException) { return Results.BadRequest(new { error = "Malformed transaction JSON." }); }
        });

        try { await _app.RunAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception ex) { _logger.LogError(ex, "Public node API failed to bind port {Port}", _options.Port + 1001); throw; }
    }

    public override async Task StopAsync(CancellationToken token)
    {
        if (_app is not null) await _app.StopAsync(token);
        await base.StopAsync(token);
    }

    private static bool HasReference(string payload, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected)) return true;
        try
        {
            using var json = JsonDocument.Parse(payload);
            return json.RootElement.TryGetProperty("paymentReference", out var reference)
                && string.Equals(reference.GetString(), expected, StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }
}
