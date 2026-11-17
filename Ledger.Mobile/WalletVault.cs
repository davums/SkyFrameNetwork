using Skyframe.Ledger.Core.Wallet;
using Microsoft.Maui.Storage;

namespace Ledger.Mobile;

public sealed class WalletVault
{
    private const string MnemonicKey = "skyframe.wallet.mnemonic";
    private const string LegacyMnemonicKey = "kora.wallet.mnemonic";
    public async Task<string?> LoadMnemonicAsync()
    {
        var mnemonic = await SecureStorage.Default.GetAsync(MnemonicKey);
        if (!string.IsNullOrWhiteSpace(mnemonic)) return mnemonic;
        mnemonic = await SecureStorage.Default.GetAsync(LegacyMnemonicKey);
        if (!string.IsNullOrWhiteSpace(mnemonic))
        {
            await SecureStorage.Default.SetAsync(MnemonicKey, mnemonic);
            SecureStorage.Default.Remove(LegacyMnemonicKey);
        }
        return mnemonic;
    }

    public async Task<string> SaveMnemonicAsync(string input)
    {
        var normalized = WalletHelper.NormalizeMnemonic(input);
        await SecureStorage.Default.SetAsync(MnemonicKey, normalized);
        SecureStorage.Default.Remove(LegacyMnemonicKey);
        return normalized;
    }

    public Task ClearAsync()
    {
        SecureStorage.Default.Remove(MnemonicKey);
        SecureStorage.Default.Remove(LegacyMnemonicKey);
        return Task.CompletedTask;
    }
}
