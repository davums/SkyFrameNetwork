using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skyframe.Ledger.Core;
using Skyframe.Ledger.Core.Models;

namespace Skyframe.Ledger.Node.Networking;

public sealed record PeerMessage(string Type, string? GenesisHash = null, long BlockHeight = 0, Block? Block = null, Transaction? Transaction = null, List<string>? Peers = null);

public sealed class PeerReputationManager
{
    private readonly ConcurrentDictionary<string, int> _scores = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _banned = new(StringComparer.OrdinalIgnoreCase);
    public int GetScore(string ip) => _scores.GetOrAdd(ip, 100);
    public bool IsBanned(string ip) => _banned.ContainsKey(ip);
    public int ReportInvalid(string ip)
    {
        var score = _scores.AddOrUpdate(ip, 50, static (_, current) => current - 50);
        if (score <= 0) _banned[ip] = 0;
        return score;
    }
}

public sealed class PeerNetwork
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new JsonStringEnumConverter() } };
    private readonly NodeOptions _options; private readonly NodeStorage _storage; private readonly NodeRuntime _runtime; private readonly NodeFileLog _log;
    private readonly PeerReputationManager _reputation = new();
    private readonly ConcurrentDictionary<string, PeerConnection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _inboundSlots = new(32, 32);
    private TcpListener? _listener; private CancellationTokenSource? _cts; private Task? _acceptLoop; private Task? _maintainLoop;
    public event Func<PeerMessage, Task>? MessageReceived;
    public PeerNetwork(NodeOptions options, NodeStorage storage, NodeRuntime runtime, NodeFileLog log) { _options=options; _storage=storage; _runtime=runtime; _log=log; }
    public int ActivePeers => _connections.Count;
    public int OutboundPeers => _connections.Values.Count(p => !p.Inbound);
    public int InboundPeers => _connections.Values.Count(p => p.Inbound);
    public IReadOnlyCollection<string> PeerEndpoints => _connections.Keys.ToArray();
    public PeerReputationManager Reputation => _reputation;
    public Task StartAsync(CancellationToken token)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(token); _listener = new TcpListener(IPAddress.Any, _options.Port); _listener.Start(32);
        _acceptLoop = AcceptLoopAsync(_cts.Token); _maintainLoop = MaintainLoopAsync(_cts.Token); return Task.CompletedTask;
    }
    public async Task StopAsync()
    {
        if (_cts is null) return; _cts.Cancel(); _listener?.Stop();
        foreach(var c in _connections.Values) c.Dispose(); _connections.Clear();
        try { if(_acceptLoop is not null) await _acceptLoop; } catch { } try { if(_maintainLoop is not null) await _maintainLoop; } catch { }
        _cts.Dispose(); _cts=null;
    }
    public async Task GossipAsync(PeerMessage message,CancellationToken token)
    {
        foreach(var c in _connections.Values) try { await c.SendAsync(message,Magic,token); } catch { c.Dispose(); }
    }
    private byte[] Magic => _options.Testnet ? NetworkConstants.TestnetMagicBytes : NetworkConstants.MainnetMagicBytes;
    private async Task AcceptLoopAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            TcpClient client; try { client=await _listener!.AcceptTcpClientAsync(token); } catch(OperationCanceledException){break;} catch(SocketException){break;}
            var ip=(client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "unknown";
            if(_reputation.IsBanned(ip) || !_inboundSlots.Wait(0)){client.Dispose();continue;}
            _=HandleConnectionAsync(client,ip,true,token).ContinueWith(_=>_inboundSlots.Release(),TaskScheduler.Default);
        }
    }
    private async Task MaintainLoopAsync(CancellationToken token)
    {
        while(!token.IsCancellationRequested)
        {
            try
            {
                var known=await _storage.LoadPeersAsync(token); if(_options.Seed is not null) known.Insert(0,_options.Seed);
                var outbound=_connections.Values.Count(p=>!p.Inbound); var subnets=_connections.Values.Where(p=>!p.Inbound).Select(p=>Subnet(p.Ip)).ToList();
                foreach(var peer in known.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if(outbound>=8 || token.IsCancellationRequested) break;
                    if(_connections.ContainsKey(peer) || !TryParseEndpoint(peer,out var host,out var port)) continue;
                    var ip=await ResolveIpAsync(host,token); if(ip is null || _reputation.IsBanned(ip) || subnets.Count(s=>s==Subnet(ip))>=2) continue;
                    var client=new TcpClient(); try { await client.ConnectAsync(host,port,token); subnets.Add(Subnet(ip)); outbound++; _=HandleConnectionAsync(client,peer,false,token,ip); } catch { client.Dispose(); }
                }
                _runtime.ActivePeers=ActivePeers; await Task.Delay(TimeSpan.FromSeconds(15),token);
            }
            catch(OperationCanceledException){break;} catch(Exception ex){await _log.WriteAsync($"Peer maintenance error: {ex.Message}"); try{await Task.Delay(5000,token);}catch{break;}}
        }
    }
    private async Task HandleConnectionAsync(TcpClient client,string endpoint,bool inbound,CancellationToken token,string? resolvedIp=null)
    {
        var ip=resolvedIp??endpoint.Split(':')[0]; var key=client.Client.RemoteEndPoint?.ToString() ?? endpoint;
        using(client) using(var connection=new PeerConnection(client,ip,inbound))
        {
            if(!_connections.TryAdd(key,connection)) return;
            try
            {
                await connection.SendAsync(new PeerMessage("HELLO",NetworkConstants.OfficialGenesisHash,_runtime.BlockHeight),Magic,token);
                var knownPeers=await _storage.LoadPeersAsync(token);if(_options.Seed is not null)knownPeers.Add(_options.Seed);if(knownPeers.Count>0)await connection.SendAsync(new PeerMessage("PEERS",Peers:knownPeers.Distinct(StringComparer.OrdinalIgnoreCase).ToList()),Magic,token);
                while(!token.IsCancellationRequested && client.Connected)
                {
                    PeerMessage? message; try{message=await connection.ReceiveAsync(Magic,token);}catch(InvalidDataException){_reputation.ReportInvalid(ip);break;}
                    if(message is null) break;
                    if(message.Type=="HELLO" && !string.Equals(message.GenesisHash,NetworkConstants.OfficialGenesisHash,StringComparison.OrdinalIgnoreCase)){_reputation.ReportInvalid(ip);break;}
                    if(message.Type is not ("HELLO" or "GET_BLOCKS" or "BLOCK" or "NEW_TX" or "PEERS")){_reputation.ReportInvalid(ip);break;}
                    if(message.Type=="PEERS" && message.Peers is not null) await _storage.SavePeersAsync(message.Peers,token);
                    if(message.Type=="GET_BLOCKS") await SendBlocksAsync(connection,message.BlockHeight,token);
                    var handler=MessageReceived; if(handler is not null) await handler(message);
                }
            }
            catch(OperationCanceledException){} catch(IOException){} catch(SocketException){}
            finally{_connections.TryRemove(key,out _);}
        }
    }
    private async Task SendBlocksAsync(PeerConnection connection,long afterHeight,CancellationToken token)
    {
        var (_,blocks)=await _storage.LoadLedgerAsync(token); foreach(var b in blocks.Where(b=>b.BlockHeight>afterHeight)) await connection.SendAsync(new PeerMessage("BLOCK",BlockHeight:b.BlockHeight,Block:b),Magic,token);
    }
    private static string Subnet(string ip){var p=ip.Split('.');return p.Length==4?string.Join('.',p[0],p[1]):ip;}
    private static bool TryParseEndpoint(string text,out string host,out int port){host="";port=0;var i=text.LastIndexOf(':');if(i<1||!int.TryParse(text[(i+1)..],out port))return false;host=text[..i];return port is >0 and <65536;}
    private static async Task<string?> ResolveIpAsync(string host,CancellationToken token){if(IPAddress.TryParse(host,out var ip))return ip.ToString();try{return(await Dns.GetHostAddressesAsync(host,token)).FirstOrDefault()?.ToString();}catch{return null;}}
    private sealed class PeerConnection:IDisposable
    {
        private const int MaxBytes=4*1024*1024; private readonly TcpClient _client; private readonly NetworkStream _stream; private readonly SemaphoreSlim _sendGate=new(1,1);
        public string Ip{get;} public bool Inbound{get;}
        public PeerConnection(TcpClient client,string ip,bool inbound){_client=client;_stream=client.GetStream();Ip=ip;Inbound=inbound;}
        public async Task SendAsync(PeerMessage message,byte[] magic,CancellationToken token)
        {
            var body=JsonSerializer.SerializeToUtf8Bytes(message,JsonOptions);if(body.Length>MaxBytes)throw new InvalidDataException("Message too large.");
            var header=new byte[8];magic.CopyTo(header,0);System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4),body.Length);
            await _sendGate.WaitAsync(token);try{await _stream.WriteAsync(header,token);await _stream.WriteAsync(body,token);await _stream.FlushAsync(token);}finally{_sendGate.Release();}
        }
        public async Task<PeerMessage?> ReceiveAsync(byte[] magic,CancellationToken token)
        {
            var header=new byte[8];if(!await ReadExactlyAsync(header,token))return null;if(!header.AsSpan(0,4).SequenceEqual(magic))throw new InvalidDataException("Bad network magic.");
            var length=System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(4));if(length is <1 or >MaxBytes)throw new InvalidDataException("Invalid frame length.");
            var body=new byte[length];if(!await ReadExactlyAsync(body,token))throw new InvalidDataException("Truncated frame.");
            try{return JsonSerializer.Deserialize<PeerMessage>(body,JsonOptions)??throw new InvalidDataException("Invalid message JSON.");}catch(JsonException ex){throw new InvalidDataException("Invalid message JSON.",ex);}
        }
        private async Task<bool> ReadExactlyAsync(Memory<byte> buffer,CancellationToken token){var read=0;while(read<buffer.Length){var n=await _stream.ReadAsync(buffer[read..],token);if(n==0)return false;read+=n;}return true;}
        public void Dispose(){try{_client.Dispose();}catch{} _sendGate.Dispose();}
    }
}






