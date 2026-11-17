using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skyframe.Ledger.Core.Models;
using NSec.Cryptography;

namespace Skyframe.Ledger.Core.Consensus;

public sealed class AccountState
{
    public long BalanceAtomic { get; set; }
    public long NextNonce { get; set; }
    public string PublicKeyHex { get; set; } = "";
}

public sealed class LedgerState
{
    public Dictionary<string, AccountState> Accounts { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, EscrowRecord> Escrows { get; } = new(StringComparer.Ordinal);
    public long TotalSupplyAtomic { get; set; }
    public LedgerState Clone()
    {
        var clone = new LedgerState { TotalSupplyAtomic = TotalSupplyAtomic };
        foreach (var (k,v) in Accounts) clone.Accounts[k] = new AccountState { BalanceAtomic=v.BalanceAtomic, NextNonce=v.NextNonce, PublicKeyHex=v.PublicKeyHex };
        foreach (var (k,v) in Escrows) clone.Escrows[k] = new EscrowRecord { EscrowId=v.EscrowId, PaymentReference=v.PaymentReference, SellerAddress=v.SellerAddress, BuyerAddress=v.BuyerAddress, ArbiterAddress=v.ArbiterAddress, AmountAtomic=v.AmountAtomic, SecretHash=v.SecretHash, ExpiryTimestampUtc=v.ExpiryTimestampUtc, IsDisputed=v.IsDisputed, Status=v.Status, DisputeStartedTimestampUtc=v.DisputeStartedTimestampUtc, FrozenDurationSeconds=v.FrozenDurationSeconds };
        return clone;
    }
}

public static class ConsensusEngine
{
    public const long MaxSupplyAtomic = 2_100_000_000_000_000L;
    public const long BlockIntervalSeconds = 30;
    public const long MinFeeAtomic = 100_000L;
    public const long DustLimitAtomic = 1_000_000L;
    private const long HalvingInterval = 1_051_200L;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = null };

    public static long GetBlockReward(long blockHeight)
    {
        if (blockHeight < 1) return 0;
        var halvings = blockHeight / HalvingInterval;
        return halvings >= 63 ? 0 : 1_000_000_000L >> (int)halvings;
    }

    public static string ComputeBlockHash(Block block)
    {
        var canonical = new { MagicBytes=Convert.ToHexString(block.MagicBytes), block.BlockHeight, block.PrevBlockHash, block.TimestampUtc, block.ProposerAddress,
            Transactions=block.Transactions.Select(t => new { t.TxId, Type=t.Type.ToString(), t.Nonce, t.FromAddress, t.ToAddress, t.AmountAtomic, t.FeeAtomic, t.TimestampUtc, t.PayloadJson, t.Signature }).ToArray() };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(canonical, JsonOptions))).ToLowerInvariant();
    }

    public static bool ValidateBlock(Block block, Block prevBlock, LedgerState state, out string error)
    {
        ArgumentNullException.ThrowIfNull(block); ArgumentNullException.ThrowIfNull(prevBlock); ArgumentNullException.ThrowIfNull(state);
        error = "";
        if (!block.MagicBytes.AsSpan().SequenceEqual(NetworkConstants.MainnetMagicBytes) && !block.MagicBytes.AsSpan().SequenceEqual(NetworkConstants.TestnetMagicBytes)) return Fail("Invalid network magic bytes.", out error);
        if (!string.Equals(block.PrevBlockHash, prevBlock.BlockHash, StringComparison.OrdinalIgnoreCase)) return Fail("Previous block hash mismatch.", out error);
        if (block.BlockHeight != prevBlock.BlockHeight + 1) return Fail("Block height is not sequential.", out error);
        if (Math.Abs(block.TimestampUtc - DateTimeOffset.UtcNow.ToUnixTimeSeconds()) > 120) return Fail("Block timestamp is outside the allowed window.", out error);
        if (block.Transactions.Count == 0 || block.Transactions[0].Type != TransactionType.Coinbase) return Fail("First transaction must be coinbase.", out error);
        if (block.Transactions.Skip(1).Any(t => t.Type == TransactionType.Coinbase)) return Fail("Block contains multiple coinbase transactions.", out error);
        long fees = 0;
        foreach (var tx in block.Transactions.Skip(1))
        {
            if (tx.FeeAtomic < MinFeeAtomic || tx.AmountAtomic < 0) return Fail("Transaction fee or amount is below the allowed minimum.", out error);
            if (fees > long.MaxValue - tx.FeeAtomic) return Fail("Fee total overflow.", out error);
            fees += tx.FeeAtomic;
        }
        var coinbase = block.Transactions[0];
        var reward = GetBlockReward(block.BlockHeight);
        if (coinbase.AmountAtomic != reward + fees || coinbase.FeeAtomic != 0) return Fail("Coinbase amount must equal the block reward plus transaction fees.", out error);
        var working = state.Clone();
        foreach (var tx in block.Transactions.Skip(1))
        {
            if (!working.Accounts.TryGetValue(tx.FromAddress, out var account)) return Fail($"Unknown sender: {tx.FromAddress}.", out error);
            if (tx.Nonce != account.NextNonce) return Fail($"Invalid nonce for {tx.FromAddress}.", out error);
            if (tx.Type is TransactionType.Transfer or TransactionType.EscrowLock && tx.AmountAtomic < DustLimitAtomic) return Fail("Transfer and escrow amounts must meet the dust limit.", out error);
            if (account.BalanceAtomic < tx.AmountAtomic + tx.FeeAtomic) return Fail("Insufficient balance.", out error);
            if (!VerifySignature(tx, account.PublicKeyHex)) return Fail($"Invalid Ed25519 signature for {tx.TxId}.", out error);
            account.BalanceAtomic -= tx.AmountAtomic + tx.FeeAtomic; account.NextNonce++;
            if (!ApplyTransaction(tx, working, block.TimestampUtc, out var escrowError)) return Fail(escrowError, out error);
        }
        var cbAddress = coinbase.ToAddress;
        if (!working.Accounts.TryGetValue(cbAddress, out var miner)) working.Accounts[cbAddress] = miner = new AccountState();
        miner.BalanceAtomic = checked(miner.BalanceAtomic + coinbase.AmountAtomic);
        working.TotalSupplyAtomic = checked(working.TotalSupplyAtomic + reward);
        if (working.TotalSupplyAtomic > MaxSupplyAtomic) return Fail("Maximum supply would be exceeded.", out error);
        // Commit the validated copy atomically.
        state.Accounts.Clear(); foreach (var p in working.Accounts) state.Accounts[p.Key] = p.Value;
        state.Escrows.Clear(); foreach (var p in working.Escrows) state.Escrows[p.Key] = p.Value;
        state.TotalSupplyAtomic = working.TotalSupplyAtomic;
        return true;
    }

    private static bool Fail(string reason, out string error) { error = reason; return false; }

    private static bool VerifySignature(Transaction tx, string publicKeyHex)
    {
        try
        {
            var keyBytes = Convert.FromHexString(publicKeyHex); var signature = Convert.FromHexString(tx.Signature);
            var publicKey = PublicKey.Import(SignatureAlgorithm.Ed25519, keyBytes, KeyBlobFormat.RawPublicKey);
            return SignatureAlgorithm.Ed25519.Verify(publicKey, Encoding.UTF8.GetBytes(tx.GetSigningPayload()), signature);
        }
        catch { return false; }
    }

    private static bool ApplyTransaction(Transaction tx, LedgerState state, long now, out string error)
    {
        error = "";
        try
        {
            switch (tx.Type)
            {
                case TransactionType.Transfer:
                    Credit(state, tx.ToAddress, tx.AmountAtomic); return true;
                case TransactionType.EscrowLock:
                {
                    var payload = JsonSerializer.Deserialize<EscrowRecord>(tx.PayloadJson) ?? throw new InvalidOperationException("Escrow payload is missing.");
                    if (string.IsNullOrWhiteSpace(payload.EscrowId) || state.Escrows.ContainsKey(payload.EscrowId) || payload.BuyerAddress != tx.FromAddress || payload.SellerAddress != tx.ToAddress || payload.AmountAtomic != tx.AmountAtomic || payload.AmountAtomic < DustLimitAtomic || payload.ExpiryTimestampUtc <= now || !ValidPaymentReference(payload.PaymentReference)) throw new InvalidOperationException("Invalid escrow lock fields.");
                    payload.Status=EscrowStatus.Locked; payload.IsDisputed=false; state.Escrows.Add(payload.EscrowId,payload); return true;
                }
                case TransactionType.EscrowClaim:
                {
                    var (e,p)=GetEscrow(tx,state);
                    if(e.Status!=EscrowStatus.Locked || e.IsDisputed) throw new InvalidOperationException("Escrow is not claimable while disputed or after completion.");
                    if(tx.FromAddress==e.BuyerAddress && Expired(e,now)) { e.Status=EscrowStatus.Refunded; Credit(state,e.BuyerAddress,e.AmountAtomic); return true; }
                    if(tx.FromAddress!=e.SellerAddress || Expired(e,now) || !SecretMatches(e.SecretHash,p)) throw new InvalidOperationException("Escrow claim conditions failed.");
                    e.Status=EscrowStatus.Claimed; Credit(state,e.SellerAddress,e.AmountAtomic); return true;
                }
                case TransactionType.EscrowDispute:
                {
                    var (e,_)=GetEscrow(tx,state); if(e.Status!=EscrowStatus.Locked || e.IsDisputed || tx.FromAddress!=e.BuyerAddress && tx.FromAddress!=e.SellerAddress) throw new InvalidOperationException("Escrow dispute conditions failed.");
                    e.IsDisputed=true; e.DisputeStartedTimestampUtc=now; return true;
                }
                case TransactionType.EscrowResolve:
                {
                    var (e,p)=GetEscrow(tx,state); if(e.Status!=EscrowStatus.Locked || !e.IsDisputed || tx.FromAddress!=e.ArbiterAddress) throw new InvalidOperationException("Escrow resolution conditions failed.");
                    var recipient=p.TryGetProperty("recipient",out var r)?r.GetString():null; if(recipient!=e.BuyerAddress && recipient!=e.SellerAddress) throw new InvalidOperationException("Resolution recipient must be the buyer or seller.");
                    e.Status=EscrowStatus.Resolved; e.IsDisputed=false; if(e.DisputeStartedTimestampUtc is long started) e.FrozenDurationSeconds += Math.Max(0,now-started); e.DisputeStartedTimestampUtc=null; Credit(state,recipient!,e.AmountAtomic); return true;
                }
                default: error="Unsupported transaction type."; return false;
            }
        }
        catch(Exception ex) { error=ex.Message; return false; }
    }

    private static (EscrowRecord,JsonElement) GetEscrow(Transaction tx,LedgerState state)
    {
        using var doc=JsonDocument.Parse(tx.PayloadJson); var id=doc.RootElement.GetProperty("escrowId").GetString() ?? doc.RootElement.GetProperty("EscrowId").GetString()!;
        if(!state.Escrows.TryGetValue(id,out var escrow)) throw new InvalidOperationException("Escrow not found."); return (escrow,doc.RootElement.Clone());
    }
    private static bool Expired(EscrowRecord e,long now) => !e.IsDisputed && now > e.ExpiryTimestampUtc + e.FrozenDurationSeconds;
    private static bool SecretMatches(string expected,JsonElement payload)
    {
        if(!payload.TryGetProperty("secret",out var s) && !payload.TryGetProperty("Secret",out s)) return false;
        var actual=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.GetString() ?? ""))).ToLowerInvariant(); return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected.ToLowerInvariant()),Encoding.UTF8.GetBytes(actual));
    }
    private static bool ValidPaymentReference(string reference) => System.Text.RegularExpressions.Regex.IsMatch(reference ?? "", "^SKYF-[0-9A-F]{6}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static void Credit(LedgerState state,string address,long amount) { if(!state.Accounts.TryGetValue(address,out var account)) state.Accounts[address]=account=new AccountState(); account.BalanceAtomic=checked(account.BalanceAtomic+amount); }
}



