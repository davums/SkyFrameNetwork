using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skyframe.Ledger.Core;
using Skyframe.Ledger.Core.Consensus;
using Skyframe.Ledger.Core.Models;
using Skyframe.Ledger.Core.Wallet;
using Skyframe.Ledger.Node.Networking;
using NSec.Cryptography;
namespace Skyframe.Ledger.Node;
public sealed class NodeWorker:BackgroundService
{
 private readonly NodeOptions _options; private readonly NodeRuntime _runtime; private readonly NodeStorage _storage; private readonly NodeFileLog _log; private readonly PeerNetwork _peers;
 private DateTimeOffset _uptimeStarted; private WalletKeypair? _wallet; private string _walletPublicKeyHex=""; private LedgerState _state=new(); private List<Block> _blocks=[]; private DateTimeOffset _lastPropose=DateTimeOffset.MinValue; private readonly SemaphoreSlim _ledgerGate=new(1,1); private bool _upnpMapped;
 public NodeWorker(NodeOptions options,NodeRuntime runtime,NodeStorage storage,NodeFileLog log,PeerNetwork peers){_options=options;_runtime=runtime;_storage=storage;_log=log;_peers=peers;_peers.MessageReceived+=OnPeerMessageAsync;}
 protected override async Task ExecuteAsync(CancellationToken stoppingToken)
 {
  await _storage.InitializeAsync(stoppingToken);var standbyLogged=false;await _log.WriteAsync($"Ledger.Node starting. P2P port {_options.Port}; IPC port {_options.Port+1000}; testnet={_options.Testnet}.");
  while(!stoppingToken.IsCancellationRequested)
  {
   try
   {
    if(!_runtime.IsRunning)
    {
     if(!_storage.HasWalletIdentity){if(!standbyLogged){await _log.WriteAsync("STANDBY: Waiting for wallet onboarding via Ledger.Dashboard");standbyLogged=true;}await Task.Delay(1000,stoppingToken);continue;}
     await Task.Delay(500,stoppingToken);continue;
    }
    if(!_storage.HasWalletIdentity){_runtime.SignalStop();continue;}
    using var config=await _storage.LoadWalletConfigAsync(stoppingToken)??throw new InvalidDataException("node_identity.json is invalid or failed authentication.");
    var mnemonic=GetString(config.RootElement,"mnemonic")??GetString(config.RootElement,"Mnemonic");if(string.IsNullOrWhiteSpace(mnemonic))throw new InvalidDataException("wallet_config.json node_identity.json must contain a mnemonic field.");
    if(_upnpMapped)UpnpPortForwarder.CloseTcp(_options.Port);_wallet?.Dispose();_wallet=WalletHelper.DeriveKeypair(mnemonic);_walletPublicKeyHex=Convert.ToHexString(_wallet.PublicKey).ToLowerInvariant();_runtime.WalletAddress=_wallet.PublicAddress;
    var loaded=await _storage.LoadLedgerAsync(stoppingToken);_state=loaded.State;_blocks=loaded.Blocks;_runtime.BlockHeight=_blocks.LastOrDefault()?.BlockHeight??0;if(_uptimeStarted==default)_uptimeStarted=DateTimeOffset.UtcNow;
    await _peers.StartAsync(stoppingToken);_upnpMapped=UpnpPortForwarder.TryOpenTcp(_options.Port,out var upnpMessage);await _log.WriteAsync(upnpMessage);await _peers.GossipAsync(new PeerMessage("GET_BLOCKS",BlockHeight:_runtime.BlockHeight),stoppingToken);
    await _log.WriteAsync($"Node active as {_wallet.PublicAddress}.");standbyLogged=false;
    var runVersion=_runtime.RestartVersion;while(_runtime.IsRunning&&_runtime.RestartVersion==runVersion&&!stoppingToken.IsCancellationRequested){_runtime.ActivePeers=_peers.ActivePeers;_runtime.OutboundPeers=_peers.OutboundPeers;_runtime.InboundPeers=_peers.InboundPeers;_runtime.UptimeSeconds=(long)(DateTimeOffset.UtcNow-_uptimeStarted).TotalSeconds;_runtime.EligibleToPropose=_options.Testnet||_peers.ActivePeers<5||((_state.Accounts.TryGetValue(_wallet!.PublicAddress,out var eligibleAccount)?eligibleAccount.BalanceAtomic:0)>=5_000_000_000L&&DateTimeOffset.UtcNow-_uptimeStarted>=TimeSpan.FromHours(24));if(DateTimeOffset.UtcNow-_lastPropose>=TimeSpan.FromSeconds(ConsensusEngine.BlockIntervalSeconds)){_lastPropose=DateTimeOffset.UtcNow;await TryProposeBlockAsync(stoppingToken);}await Task.Delay(500,stoppingToken);}
    await _peers.StopAsync();if(_upnpMapped){UpnpPortForwarder.CloseTcp(_options.Port);_upnpMapped=false;}await _log.WriteAsync("Node stopped by control API.");
   }
   catch(OperationCanceledException)when(stoppingToken.IsCancellationRequested){break;}
   catch(Exception ex){_runtime.LastError=ex.Message;_runtime.SignalStop();await _peers.StopAsync();await _log.WriteAsync($"Node standby after error: {ex.Message}");await Task.Delay(1000,stoppingToken);}
  }
  if(_upnpMapped)UpnpPortForwarder.CloseTcp(_options.Port);_wallet?.Dispose();
 }
 private async Task TryProposeBlockAsync(CancellationToken token)
 {
  if(_wallet is null)return;var balance=_state.Accounts.TryGetValue(_wallet.PublicAddress,out var a)?a.BalanceAtomic:0;
  if(!_options.Testnet&&_peers.ActivePeers>=5&&(balance<5_000_000_000L||DateTimeOffset.UtcNow-_uptimeStarted<TimeSpan.FromHours(24)))return;
  var height=(_blocks.LastOrDefault()?.BlockHeight??0)+1;var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var reward=ConsensusEngine.GetBlockReward(height);var parent=_blocks.LastOrDefault()??GenesisParent();var selected=new List<Transaction>();long fees=0;
  while(_runtime.TryDequeue(out var tx))
  {
   if(selected.Count>=500){_runtime.Requeue(tx);break;}var candidate=selected.Append(tx).ToList();var candidateFees=checked(fees+tx.FeeAtomic);var trial=CreateBlock(parent,height,now,checked(reward+candidateFees),candidate);var state=_state.Clone();
   if(ConsensusEngine.ValidateBlock(trial,parent,state,out _)){selected.Add(tx);fees=candidateFees;}else await _log.WriteAsync($"Dropped invalid mempool transaction {tx.TxId}.");
  }
  var block=CreateBlock(parent,height,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),checked(reward+fees),selected);var working=_state.Clone();
  if(!ConsensusEngine.ValidateBlock(block,parent,working,out var error)){await _log.WriteAsync($"Block proposal rejected: {error}");foreach(var tx in selected)_runtime.Requeue(tx);return;}
  block.BlockHash=ConsensusEngine.ComputeBlockHash(block);block.BlockSignature=Convert.ToHexString(_wallet.Sign(Encoding.UTF8.GetBytes(block.BlockHash))).ToLowerInvariant();
  await _ledgerGate.WaitAsync(token);try{_state=working;_blocks.Add(block);_runtime.BlockHeight=height;await _storage.SaveLedgerAsync(_state,_blocks,token);}finally{_ledgerGate.Release();}
  await _peers.GossipAsync(new PeerMessage("BLOCK",BlockHeight:height,Block:block),token);await _log.WriteAsync($"Proposed block {height} ({selected.Count} transactions).");
 }
 private Block CreateBlock(Block parent,long height,long timestamp,long amount,List<Transaction> transactions)
 {var coinbase=new Transaction{TxId=Guid.NewGuid().ToString("N"),Type=TransactionType.Coinbase,ToAddress=_wallet!.PublicAddress,AmountAtomic=amount,TimestampUtc=timestamp};return new Block{MagicBytes=(_options.Testnet?NetworkConstants.TestnetMagicBytes:NetworkConstants.MainnetMagicBytes).ToArray(),BlockHeight=height,PrevBlockHash=parent.BlockHash,TimestampUtc=timestamp,ProposerAddress=_wallet.PublicAddress,ProposerPublicKeyHex=_walletPublicKeyHex,Transactions=[coinbase,..transactions]};}
 private static Block GenesisParent()=>new(){BlockHeight=0,BlockHash=NetworkConstants.OfficialGenesisHash,TimestampUtc=NetworkConstants.GenesisTimestampUtc};
 private async Task OnPeerMessageAsync(PeerMessage message)
 {if(message.Type=="NEW_TX"&&message.Transaction is not null){if(_runtime.TryEnqueue(message.Transaction))await _peers.GossipAsync(new PeerMessage("NEW_TX",Transaction:message.Transaction),CancellationToken.None);}else if(message.Type=="BLOCK"&&message.Block is not null)await AcceptBlockAsync(message.Block);else if(message.Type=="HELLO"&&message.BlockHeight>_runtime.BlockHeight)await _peers.GossipAsync(new PeerMessage("GET_BLOCKS",BlockHeight:_runtime.BlockHeight),CancellationToken.None);}
 private async Task AcceptBlockAsync(Block block)
 {
  await _ledgerGate.WaitAsync();try
  {var parent=_blocks.LastOrDefault()??GenesisParent();if(!block.MagicBytes.AsSpan().SequenceEqual(_options.Testnet?NetworkConstants.TestnetMagicBytes:NetworkConstants.MainnetMagicBytes)||block.BlockHash!=ConsensusEngine.ComputeBlockHash(block)||!VerifyBlockSignature(block)){await _log.WriteAsync($"Rejected block {block.BlockHeight}: invalid hash or proposer signature.");return;}
   var next=_state.Clone();if(!ConsensusEngine.ValidateBlock(block,parent,next,out var error)){await _log.WriteAsync($"Rejected block {block.BlockHeight}: {error}");return;}
   _state=next;_blocks.Add(block);_runtime.BlockHeight=block.BlockHeight;await _storage.SaveLedgerAsync(_state,_blocks,CancellationToken.None);await _peers.GossipAsync(new PeerMessage("BLOCK",BlockHeight:block.BlockHeight,Block:block),CancellationToken.None);await _log.WriteAsync($"Accepted block {block.BlockHeight}.");
  }finally{_ledgerGate.Release();}
 }
 private static bool VerifyBlockSignature(Block b)
 {try{var pk=Convert.FromHexString(b.ProposerPublicKeyHex);var h=SHA256.HashData(pk);if(WalletHelper.AddressPrefix+Convert.ToHexString(h.AsSpan(0,20)).ToLowerInvariant()!=b.ProposerAddress)return false;var key=PublicKey.Import(SignatureAlgorithm.Ed25519,pk,KeyBlobFormat.RawPublicKey);return SignatureAlgorithm.Ed25519.Verify(key,Encoding.UTF8.GetBytes(b.BlockHash),Convert.FromHexString(b.BlockSignature));}catch{return false;}}
 private static string? GetString(JsonElement root,string key)=>root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
}





