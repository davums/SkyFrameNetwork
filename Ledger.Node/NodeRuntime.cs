using System.Collections.Concurrent;
using Skyframe.Ledger.Core.Models;

namespace Skyframe.Ledger.Node;

public sealed class NodeRuntime
{
    private readonly TaskCompletionSource _startSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _restartVersion;
    private readonly ConcurrentQueue<Transaction> _mempool = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seenTransactions = new(StringComparer.Ordinal);
    public bool IsRunning { get; private set; }
    public bool IsStandby { get; set; } = true;
    public string? WalletAddress { get; set; }
    public long BlockHeight { get; set; }
    public int ActivePeers { get; set; }
    public int OutboundPeers { get; set; }
    public int InboundPeers { get; set; }
    public long UptimeSeconds { get; set; }
    public bool EligibleToPropose { get; set; }
    public string? LastError { get; set; }
    public int RestartVersion => Volatile.Read(ref _restartVersion);
    public void SignalStart() { IsRunning = true; IsStandby = false; _startSignal.TrySetResult(); }
    public void SignalStop() { IsRunning = false; IsStandby = true; }
    public void SignalRestart() { Interlocked.Increment(ref _restartVersion); IsRunning = true; IsStandby = false; _startSignal.TrySetResult(); }
    public Task WaitForStartAsync(CancellationToken token) => _startSignal.Task.WaitAsync(token);
    public bool TryEnqueue(Transaction transaction)
    {
        var id = string.IsNullOrWhiteSpace(transaction.TxId) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(transaction.GetSigningPayload()))) : transaction.TxId;
        if (!_seenTransactions.TryAdd(id, 0)) return false;
        _mempool.Enqueue(transaction); return true;
    }
    public bool TryDequeue(out Transaction transaction) => _mempool.TryDequeue(out transaction!);
    public Transaction? FindPendingTransaction(Func<Transaction, bool> predicate) => _mempool.ToArray().FirstOrDefault(predicate);
    public void Requeue(Transaction transaction) => _mempool.Enqueue(transaction);
    public int MempoolCount => _mempool.Count;
}



