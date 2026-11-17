using System.Text.Json;
using System.Text.Json.Serialization;
using Skyframe.Ledger.Core.Models;
using Skyframe.Ledger.Node.Networking;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Json;
namespace Skyframe.Ledger.Node;
public sealed class ControlApiService:BackgroundService
{
 private static readonly JsonSerializerOptions TxJsonOptions=new(){PropertyNameCaseInsensitive=true,Converters={new JsonStringEnumConverter()}};
 private readonly NodeOptions _options;private readonly NodeRuntime _runtime;private readonly NodeStorage _storage;private readonly PeerNetwork _peers;private readonly ILogger<ControlApiService> _logger;private WebApplication? _app;
 public ControlApiService(NodeOptions options,NodeRuntime runtime,NodeStorage storage,PeerNetwork peers,ILogger<ControlApiService> logger){_options=options;_runtime=runtime;_storage=storage;_peers=peers;_logger=logger;}
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  var builder=WebApplication.CreateSlimBuilder();builder.Services.ConfigureHttpJsonOptions(o=>o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));builder.WebHost.UseUrls($"http://127.0.0.1:{_options.Port+1000}");_app=builder.Build();
  _app.MapGet("/status",()=>Results.Ok(new{genesisHash=Skyframe.Ledger.Core.NetworkConstants.OfficialGenesisHash,status=_runtime.IsRunning?"running":"standby",standby=_runtime.IsStandby,walletAddress=_runtime.WalletAddress,blockHeight=_runtime.BlockHeight,activePeers=_runtime.ActivePeers,outboundPeers=_runtime.OutboundPeers,inboundPeers=_runtime.InboundPeers,uptimeSeconds=_runtime.UptimeSeconds,rewardEligible=_runtime.EligibleToPropose,mempoolCount=_runtime.MempoolCount,testnet=_options.Testnet,lastError=_runtime.LastError}));
  _app.MapGet("/peers",()=>Results.Ok(_peers.PeerEndpoints.Select(peer=>$"http://{peer.Split(':')[0]}:{_options.Port+1000}").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
  _app.MapGet("/balance/{address}",async(string address,CancellationToken token)=>{var(s,_)=await _storage.LoadLedgerAsync(token);var atomic=s.Accounts.TryGetValue(address,out var account)?account.BalanceAtomic:0;return Results.Ok(new{address,balanceAtomic=atomic,balanceSkyf=atomic/100_000_000m,nextNonce=s.Accounts.TryGetValue(address,out account)?account.NextNonce:0});});
  _app.MapGet("/escrows",async(CancellationToken token)=>{var(s,_)=await _storage.LoadLedgerAsync(token);return Results.Ok(s.Escrows.Values);});
  _app.MapGet("/mempool/{txId}",(string txId)=>Results.Ok(new{txId,pending=_runtime.FindPendingTransaction(t=>string.Equals(t.TxId,txId,StringComparison.Ordinal)) is not null}));
  _app.MapGet("/payments/lookup",async(string address,long amountAtomic,long afterTimestampUtc,string? reference,CancellationToken token)=>
  {
   var pending=_runtime.FindPendingTransaction(t=>t.Type==TransactionType.Transfer&&t.ToAddress==address&&t.AmountAtomic==amountAtomic&&t.TimestampUtc>=afterTimestampUtc&&HasReference(t.PayloadJson,reference));
   if(pending is not null)return Results.Ok(new{status="mempool",txId=pending.TxId});
   var(_,blocks)=await _storage.LoadLedgerAsync(token);
   var confirmed=blocks.SelectMany(b=>b.Transactions).FirstOrDefault(t=>t.Type==TransactionType.Transfer&&t.ToAddress==address&&t.AmountAtomic==amountAtomic&&t.TimestampUtc>=afterTimestampUtc&&HasReference(t.PayloadJson,reference));
   return Results.Ok(new{status=confirmed is null?"none":"confirmed",txId=confirmed?.TxId});
  });
  _app.MapPost("/tx",async(HttpRequest req,CancellationToken token)=>{try{var tx=await JsonSerializer.DeserializeAsync<Transaction>(req.Body,TxJsonOptions,token);if(tx is null)return Results.BadRequest(new{error="Transaction JSON is required."});var queued=_runtime.TryEnqueue(tx);if(queued)await _peers.GossipAsync(new PeerMessage("NEW_TX",Transaction:tx),token);return Results.Accepted(value:new{tx.TxId,status=queued?"queued":"duplicate"});}catch(JsonException){return Results.BadRequest(new{error="Malformed transaction JSON."});}});
  _app.MapPost("/control/start",()=>{if(!_storage.HasWalletIdentity)return Results.Conflict(new{error="Complete wallet onboarding before starting the node."});_runtime.LastError=null;_runtime.SignalStart();return Results.Ok(new{status="start-signalled"});});
  _app.MapPost("/control/stop",()=>{_runtime.SignalStop();return Results.Ok(new{status="stop-signalled"});});
  _app.MapPost("/control/restart",()=>{if(!_storage.HasWalletIdentity)return Results.Conflict(new{error="Complete wallet onboarding before restarting the node."});_runtime.LastError=null;_runtime.SignalStop();_runtime.SignalRestart();return Results.Ok(new{status="restart-signalled"});});
  try{await _app.RunAsync(stoppingToken);}catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){}catch(Exception ex){_logger.LogError(ex,"Local control API failed to start on port {Port}",_options.Port+1000);throw;}
 }
 public override async Task StopAsync(CancellationToken token){if(_app is not null)await _app.StopAsync(token);await base.StopAsync(token);}
 private static bool HasReference(string payload,string? expected){if(string.IsNullOrWhiteSpace(expected))return true;try{using var json=JsonDocument.Parse(payload);return json.RootElement.TryGetProperty("paymentReference",out var reference)&&string.Equals(reference.GetString(),expected,StringComparison.Ordinal);}catch(JsonException){return false;}}
}


