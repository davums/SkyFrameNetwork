using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skyframe.Ledger.Core.Models;

namespace Skyframe.Ledger.Core;

public static class NetworkConstants
{
    public const string BootstrapSeedsUrl = "https://raw.githubusercontent.com/davums/SkyFrameNetwork/main/seeds.json";
    public static readonly byte[] MainnetMagicBytes = [0x53, 0x4B, 0x59, 0x46];
    public static readonly byte[] TestnetMagicBytes = [0x54, 0x45, 0x53, 0x54];
    public const long GenesisTimestampUtc = 1_794_821_520L;
    public const string GenesisMessage = "16 Nov 2026 — Skyframe: born in Johannesburg, built for the world; peer-to-peer value for everyone.";
    public static readonly string OfficialGenesisHash = ComputeGenesisHash();

    private static string ComputeGenesisHash()
    {
        // Canonical genesis header: mainnet magic, height zero, no parent, no proposer or transactions.
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            MagicBytes = Convert.ToHexString(MainnetMagicBytes),
            BlockHeight = 0L,
            PrevBlockHash = "",
            TimestampUtc = GenesisTimestampUtc,
            ProposerAddress = "",
            Transactions = Array.Empty<object>(),
            Message = GenesisMessage
        });
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
