namespace Skyframe.Ledger.Core.Models;

public enum TransactionType { Transfer, Coinbase, EscrowLock, EscrowClaim, EscrowDispute, EscrowResolve }

public sealed class Transaction
{
    public string TxId { get; set; } = "";
    public TransactionType Type { get; set; }
    public long Nonce { get; set; }
    public string FromAddress { get; set; } = "";
    public string ToAddress { get; set; } = "";
    public long AmountAtomic { get; set; }
    public long FeeAtomic { get; set; }
    public long TimestampUtc { get; set; }
    public string PayloadJson { get; set; } = "";
    public string Signature { get; set; } = "";
    public string GetSigningPayload() => string.Join("\n", new[] { Skyframe.Ledger.Core.NetworkConstants.OfficialGenesisHash, TxId, Type.ToString(), Nonce.ToString(System.Globalization.CultureInfo.InvariantCulture), FromAddress, ToAddress, AmountAtomic.ToString(System.Globalization.CultureInfo.InvariantCulture), FeeAtomic.ToString(System.Globalization.CultureInfo.InvariantCulture), TimestampUtc.ToString(System.Globalization.CultureInfo.InvariantCulture), PayloadJson });
}
public enum EscrowStatus { Locked, Claimed, Resolved, Refunded }
public sealed class EscrowRecord
{
    public string EscrowId { get; set; } = "";
    public string PaymentReference { get; set; } = "";
    public string SellerAddress { get; set; } = "";
    public string BuyerAddress { get; set; } = "";
    public string ArbiterAddress { get; set; } = "";
    public long AmountAtomic { get; set; }
    public string SecretHash { get; set; } = "";
    public long ExpiryTimestampUtc { get; set; }
    public bool IsDisputed { get; set; }
    public EscrowStatus Status { get; set; } = EscrowStatus.Locked;
    public long? DisputeStartedTimestampUtc { get; set; }
    public long FrozenDurationSeconds { get; set; }
}
public sealed class Block
{
    public byte[] MagicBytes { get; set; } = Skyframe.Ledger.Core.NetworkConstants.MainnetMagicBytes.ToArray();
    public long BlockHeight { get; set; }
    public string PrevBlockHash { get; set; } = "";
    public long TimestampUtc { get; set; }
    public string ProposerAddress { get; set; } = "";
    public string ProposerPublicKeyHex { get; set; } = "";
    public string BlockSignature { get; set; } = "";
    public List<Transaction> Transactions { get; set; } = [];
    public string BlockHash { get; set; } = "";
}

