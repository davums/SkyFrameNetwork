using System.Security.Cryptography;
using System.Text;
using NBitcoin;
using NSec.Cryptography;

namespace Skyframe.Ledger.Core.Wallet;

public sealed class WalletKeypair : IDisposable
{
    private readonly NSec.Cryptography.Key _key;
    internal WalletKeypair(NSec.Cryptography.Key key, byte[] publicKey, string address) { _key = key; PublicKey = publicKey; PublicAddress = address; }
    public byte[] PublicKey { get; }
    public string PublicAddress { get; }
    public byte[] Sign(ReadOnlySpan<byte> payload) => SignatureAlgorithm.Ed25519.Sign(_key, payload);
    public void Dispose() => _key.Dispose();
}

public static class WalletHelper
{
    public const string AddressPrefix = "skyf1";

    public static string GenerateNewMnemonic() => new Mnemonic(Wordlist.English, WordCount.Twelve).ToString();

    public static IReadOnlyList<string> GetEnglishWordSuggestions(string prefix, int maxResults = 8)
    {
        if (maxResults < 1) return Array.Empty<string>();
        var normalizedPrefix = (prefix ?? string.Empty).Trim().ToLowerInvariant();
        return Enumerable.Range(0, Wordlist.English.WordCount)
            .Select(Wordlist.English.GetWordAtIndex)
            .Where(word => word.StartsWith(normalizedPrefix, StringComparison.Ordinal))
            .Take(maxResults)
            .ToArray();
    }

    public static string NormalizeMnemonic(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var normalized = string.Join(' ', input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
        var mnemonic = new Mnemonic(normalized, Wordlist.English);
        if (!mnemonic.IsValidChecksum) throw new ArgumentException("Mnemonic checksum is invalid.", nameof(input));
        return mnemonic.ToString();
    }

    public static WalletKeypair DeriveKeypair(string mnemonic)
    {
        var phrase = NormalizeMnemonic(mnemonic);
        var seed = new Mnemonic(phrase, Wordlist.English).DeriveSeed();
        var algorithm = SignatureAlgorithm.Ed25519;
        var key = NSec.Cryptography.Key.Import(algorithm, seed.AsSpan(0, 32), KeyBlobFormat.RawPrivateKey);
        CryptographicOperations.ZeroMemory(seed);
        var publicKey = key.PublicKey.Export(KeyBlobFormat.RawPublicKey);
        var publicKeyHash = SHA256.HashData(publicKey);
        // Hex is used as the permitted address encoding; 20 bytes keep addresses compact.
        var address = AddressPrefix + Convert.ToHexString(publicKeyHash.AsSpan(0, 20)).ToLowerInvariant();
        return new WalletKeypair(key, publicKey, address);
    }
}


