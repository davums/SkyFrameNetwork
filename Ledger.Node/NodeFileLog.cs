namespace Skyframe.Ledger.Node;

public sealed class NodeFileLog
{
    private readonly NodeStorage _storage;
    private readonly ILogger<NodeFileLog> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    public NodeFileLog(NodeStorage storage, ILogger<NodeFileLog> logger) { _storage = storage; _logger = logger; }
    public async Task WriteAsync(string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} {message}";
        _logger.LogInformation("{NodeMessage}", message);
        await _gate.WaitAsync();
        try { await File.AppendAllTextAsync(_storage.LogPath, line + Environment.NewLine); }
        finally { _gate.Release(); }
    }
}
