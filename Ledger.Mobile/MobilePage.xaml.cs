using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skyframe.Ledger.Core.Models;
using Skyframe.Ledger.Core.Wallet;
using Microsoft.Maui.Layouts;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace Ledger.Mobile;

public partial class MobilePage : ContentPage
{
    private readonly WalletVault _vault;
    private readonly NodeQuorumClient _nodes;
    private string? _mnemonic;
    private string? _address;
    private string? _nonceAddress;
    private long _nextLocalNonce;
    private bool _hasLocalNonce;
    private string _section = "Wallet";
    private Editor? _phraseEditor, _nodeEditor;
    private Entry? _verifyFirst, _verifySecond, _destination, _sendAmount;
    private string? _scannedAddress, _scannedAmount, _scannedReference;
    private int _verifyPositionOne, _verifyPositionTwo;
    private Label? _walletStatus;
    private Label? _homeBalance;
    private Label? _posStatus;
    private IDispatcherTimer? _posTimer;
    private long _posCreatedAt;
    private long _posAmountAtomic;
    private string? _posRequestReference;
    private bool _loading;

    private static readonly Color PageBackground = Color.FromArgb("#090A0C");
    private static readonly Color Surface = Color.FromArgb("#151619");
    private static readonly Color Accent = Color.FromArgb("#FFFFFF");
    private static readonly Color MainText = Color.FromArgb("#F4F4F5");
    private static readonly Color Muted = Color.FromArgb("#A1A1AA");
    private static readonly Color Good = Color.FromArgb("#34D399");
    private static readonly Color Warning = Color.FromArgb("#FBBF24");
    private static readonly Color Bad = Color.FromArgb("#FB7185");

    public MobilePage(WalletVault vault, NodeQuorumClient nodes)
    {
        InitializeComponent();
        _vault = vault;
        _nodes = nodes;
        BackgroundColor = PageBackground;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_loading) return;
        _loading = true;
        try
        {
            _mnemonic = await _vault.LoadMnemonicAsync();
            if (!string.IsNullOrWhiteSpace(_mnemonic))
            {
                using var wallet = WalletHelper.DeriveKeypair(_mnemonic);
                _address = wallet.PublicAddress;
                _nonceAddress = _address;
                _hasLocalNonce = false;
                ShowApp();
            }
            else ShowOnboarding();
        }
        catch (Exception ex) { await DisplayAlert("Secure wallet unavailable", ex.Message, "OK"); ShowOnboarding(); }
        finally { _loading = false; }
    }

    private void ShowOnboarding()
    {
        var layout = PageLayout();
        layout.Add(new Image { Source = "skyframe_logo.svg", WidthRequest = 190, HeightRequest = 190, Aspect = Aspect.AspectFit, HorizontalOptions = LayoutOptions.Center });
        layout.Add(Heading("SKYFRAME WALLET", "Your keys stay on this device."));
        layout.Add(Text("Create a new 12-word wallet or import a recovery phrase. The phrase is stored only in iOS Keychain or Android Keystore.", Muted));
        var tabs = new HorizontalStackLayout { Spacing = 8 };
        tabs.Add(ActionButton("Create wallet", () => ShowCreateWallet(), true));
        tabs.Add(ActionButton("Import wallet", () => ShowImportWallet()));
        layout.Add(tabs);
        Content = new ScrollView { Content = layout };
    }

    private void ShowCreateWallet()
    {
        var phrase = WalletHelper.GenerateNewMnemonic();
        var words = phrase.Split(' ');
        _verifyPositionOne = RandomNumberGenerator.GetInt32(words.Length);
        do { _verifyPositionTwo = RandomNumberGenerator.GetInt32(words.Length); } while (_verifyPositionTwo == _verifyPositionOne);
        var layout = PageLayout();
        layout.Add(Heading("NEW WALLET", "Back up your recovery phrase."));
        layout.Add(Text("Write these twelve words down in order and keep them private. They cannot be recovered if lost.", Muted));
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star) }, ColumnSpacing = 8, RowSpacing = 8 };
        for (var i = 0; i < words.Length; i++)
        {
            var chip = Text($"{i + 1:00}  {words[i]}", MainText, 14, true);
            chip.Padding = new Thickness(12, 10);
            chip.BackgroundColor = Surface;
            grid.Add(chip, i % 2, i / 2);
            if (grid.RowDefinitions.Count <= i / 2) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        }
        layout.Add(grid);
        layout.Add(Text($"Verify word #{_verifyPositionOne + 1} and word #{_verifyPositionTwo + 1}", MainText, 15, true));
        _verifyFirst = Input($"Word #{_verifyPositionOne + 1}");
        _verifySecond = Input($"Word #{_verifyPositionTwo + 1}");
        layout.Add(_verifyFirst);
        layout.Add(_verifySecond);
        layout.Add(ActionButton("Save securely", async () =>
        {
            if (!string.Equals(_verifyFirst.Text?.Trim(), words[_verifyPositionOne], StringComparison.OrdinalIgnoreCase)
                || !string.Equals(_verifySecond.Text?.Trim(), words[_verifyPositionTwo], StringComparison.OrdinalIgnoreCase))
            { await DisplayAlert("Check your backup", "The verification words do not match.", "OK"); return; }
            await SaveWalletAsync(phrase);
        }, true));
        layout.Add(ActionButton("Back", ShowOnboarding));
        Content = new ScrollView { Content = layout };
    }

    private void ShowImportWallet()
    {
        var layout = PageLayout();
        layout.Add(Heading("IMPORT WALLET", "Restore a 12-word phrase."));
        layout.Add(Text("The phrase is normalized and checksum-validated on this device before it is saved to SecureStorage.", Muted));
        _phraseEditor = new Editor
        {
            Placeholder = "Enter your 12 words",
            AutoSize = EditorAutoSizeOption.TextChanges,
            MinimumHeightRequest = 120,
            TextColor = MainText,
            PlaceholderColor = Muted,
            BackgroundColor = Surface
        };
        var chips = new FlexLayout { Wrap = FlexWrap.Wrap, Direction = FlexDirection.Row };
        var validity = Text("Enter 12 BIP-39 English words.", Muted);
        var save = ActionButton("Import securely", async () => await SaveWalletAsync(_phraseEditor.Text), true);
        save.IsEnabled = false;
        _phraseEditor.TextChanged += (_, _) =>
        {
            chips.Children.Clear();
            var split = (_phraseEditor.Text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var words = split.Select(w => w.ToLowerInvariant()).ToArray();
            foreach (var word in words)
            {
                var validWord = WalletHelper.GetEnglishWordSuggestions(word, 2048).Contains(word, StringComparer.Ordinal);
                chips.Children.Add(Chip(word, validWord ? Good : Bad));
            }
            validity.TextColor = Muted;
            validity.Text = $"{words.Length} of 12 words";
            save.IsEnabled = false;
            if (words.Length == 12)
            {
                try { _ = WalletHelper.NormalizeMnemonic(_phraseEditor.Text ?? ""); validity.Text = "Checksum valid."; validity.TextColor = Good; save.IsEnabled = true; }
                catch (ArgumentException ex) { validity.Text = ex.Message; validity.TextColor = Bad; }
            }
            else if (words.Length > 12) { validity.Text = "A wallet phrase must contain exactly 12 words."; validity.TextColor = Bad; }
        };
        layout.Add(_phraseEditor);
        layout.Add(chips);
        layout.Add(validity);
        layout.Add(save);
        layout.Add(ActionButton("Back", ShowOnboarding));
        Content = new ScrollView { Content = layout };
    }

    private async Task SaveWalletAsync(string? phrase)
    {
        try
        {
            _mnemonic = await _vault.SaveMnemonicAsync(phrase ?? "");
            using var wallet = WalletHelper.DeriveKeypair(_mnemonic);
            _address = wallet.PublicAddress;
            _nonceAddress = _address;
            _hasLocalNonce = false;
            ShowApp();
        }
        catch (Exception ex) { await DisplayAlert("Wallet not saved", ex.Message, "OK"); }
    }

    private void ShowApp()
    {
        _posTimer?.Stop();
        var layout = PageLayout();
        var brandHeader = new HorizontalStackLayout { Spacing = 12, VerticalOptions = LayoutOptions.Center };
        brandHeader.Add(new Image { Source = "skyframe_logo.svg", WidthRequest = 48, HeightRequest = 48, Aspect = Aspect.AspectFit });
        brandHeader.Add(Heading("SKYFRAME", "Wallet & merchant"));
        layout.Add(brandHeader);
        var nav = new FlexLayout { Wrap = FlexWrap.Wrap, Direction = FlexDirection.Row };
        foreach (var section in new[] { "Wallet", "Merchant POS", "Escrow", "Settings" })
        {
            var selected = section;
            var tab = ActionButton(section, () => { _section = selected; ShowApp(); }, selected == _section);
            tab.Margin = new Thickness(3);
            nav.Children.Add(tab);
        }
        layout.Add(nav);
        switch (_section)
        {
            case "Merchant POS": BuildMerchantPos(layout); break;
            case "Escrow": BuildEscrow(layout); break;
            case "Settings": BuildSettings(layout); break;
            default: BuildWallet(layout); break;
        }
        Content = new ScrollView { Content = layout };
        if (_section == "Wallet" && _homeBalance is not null)
            _ = RefreshBalanceAsync(_homeBalance);
    }

    private void BuildWallet(VerticalStackLayout layout)
    {
        var balance = Text("Balance: checking node quorum…", Accent, 21, true);
        _homeBalance = balance;
        _walletStatus = Text($"Gateway: {_nodes.GetSavedNodes()}", Muted);
        layout.Add(Panel(new VerticalStackLayout { Spacing = 8, Children = { balance, _walletStatus } }));
        var addressBox = new VerticalStackLayout { Spacing = 8 };
        addressBox.Add(Text("RECEIVE ADDRESS", Muted, 12, true));
        addressBox.Add(Text(_address ?? "", MainText, 13));
        addressBox.Add(new BarcodeGeneratorView { Value = $"skyframe:{_address}", Format = BarcodeFormat.QrCode, WidthRequest = 210, HeightRequest = 210, BackgroundColor = Colors.White, HorizontalOptions = LayoutOptions.Center });
        addressBox.Add(ActionButton("Copy address", async () => { if (_address is not null) await Clipboard.Default.SetTextAsync(_address); }));
        layout.Add(Panel(addressBox));

        layout.Add(Text("SEND SKYF", Accent, 14, true));
        _destination = Input("Recipient skyf1…");
        _sendAmount = Input("Amount in SKYF");
        _destination.Text = _scannedAddress;
        _sendAmount.Text = _scannedAmount;
        _sendAmount.Keyboard = Keyboard.Numeric;
        layout.Add(_destination);
        layout.Add(_sendAmount);
        var scan = ActionButton("Scan payment QR", ScanQrAsync);
        layout.Add(scan);
        layout.Add(ActionButton("Review & send", SendSkyframeAsync, true));
        layout.Add(ActionButton("Refresh balance", async () => await RefreshBalanceAsync(balance)));
    }

    private async Task RefreshBalanceAsync(Label balance)
    {
        if (string.IsNullOrWhiteSpace(_address)) return;
        balance.Text = "Balance: querying node quorum…";
        try
        {
            var result = await _nodes.GetBalanceAsync(_address);
            if (!result.Success || result.Snapshot is null)
            {
                balance.Text = "Balance unavailable";
                if (_walletStatus is not null) { _walletStatus.Text = result.Message; _walletStatus.TextColor = Bad; }
                return;
            }
            balance.Text = $"{result.Snapshot.BalanceAtomic / 100_000_000m:N4} SKYF";
            if (_walletStatus is not null)
            {
                var differentNodeWallet = result.Snapshot.BalanceAtomic == 0
                    && !string.IsNullOrWhiteSpace(result.LocalNodeWalletAddress)
                    && !string.Equals(_address, result.LocalNodeWalletAddress, StringComparison.Ordinal);
                _walletStatus.Text = differentNodeWallet
                    ? $"This wallet address has no SKYF. The local node/Dashboard wallet is {ShortAddress(result.LocalNodeWalletAddress)}. Import that same recovery phrase to see its balance."
                    : $"{result.Message} · Nonce {result.Snapshot.Nonce} · {result.Snapshot.MatchingNodes}/{result.Snapshot.CheckedNodes} nodes agree";
                _walletStatus.TextColor = differentNodeWallet ? Warning : Good;
            }
        }
        catch (Exception ex)
        {
            balance.Text = "Balance unavailable";
            if (_walletStatus is not null) { _walletStatus.Text = $"{ex.GetType().Name}: {ex.Message}"; _walletStatus.TextColor = Bad; }
        }
    }

    private async Task SendSkyframeAsync()
    {
        if (!TryAmount(_sendAmount?.Text, out var amountAtomic) || amountAtomic < 1_000_000)
        { await DisplayAlert("Invalid amount", "Enter a SKYF amount meeting the minimum transfer amount.", "OK"); return; }
        if (!IsSkyframeAddress(_destination?.Text))
        { await DisplayAlert("Invalid address", "Enter a valid skyf1 address or scan a payment QR.", "OK"); return; }
        var snapshot = await GetAccountSnapshotAsync();
        if (snapshot is null) return;
        var paymentReference = string.Equals(_destination.Text?.Trim(), _scannedAddress, StringComparison.Ordinal) ? _scannedReference : null;
        var transferPayload = string.IsNullOrWhiteSpace(paymentReference) ? "" : JsonSerializer.Serialize(new { paymentReference });
        var tx = CreateSignedTransaction(TransactionType.Transfer, _destination!.Text!.Trim(), amountAtomic, snapshot.Nonce, transferPayload);
        var result = await _nodes.BroadcastAsync(tx);
        await DisplayAlert(result.Accepted ? "Payment sent" : "Payment status", result.Message, "OK");
        if (result.Accepted) { _scannedReference = null; _scannedAddress = null; _scannedAmount = null; }
        if (_walletStatus is not null) _walletStatus.Text = result.Message;
    }

    private void BuildMerchantPos(VerticalStackLayout layout)
    {
        layout.Add(Text("MERCHANT PAYMENT REQUEST", Accent, 14, true));
        layout.Add(Text("Enter the local price and a SKYF-per-ZAR quote supplied by your business. No exchange rate is fetched or assumed.", Muted));
        var currency = new Picker { Title = "Price currency", TextColor = MainText, BackgroundColor = Surface, ItemsSource = new[] { "SKYF", "ZAR" }, SelectedIndex = 0 };
        var amount = Input("Price");
        amount.Keyboard = Keyboard.Numeric;
        var rate = Input("SKYF per 1 ZAR (required for ZAR prices)");
        rate.Keyboard = Keyboard.Numeric;
        layout.Add(currency);
        layout.Add(amount);
        layout.Add(rate);
        var create = ActionButton("Create payment QR", async () =>
        {
            if (!decimal.TryParse(amount.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) || price <= 0)
            { await DisplayAlert("Price required", "Enter a valid positive price.", "OK"); return; }
            decimal skyframeAmount = price;
            if (currency.SelectedIndex == 1)
            {
                if (!decimal.TryParse(rate.Text, NumberStyles.Number, CultureInfo.InvariantCulture, out var skyframePerZar) || skyframePerZar <= 0)
                { await DisplayAlert("Exchange quote required", "Enter the SKYF-per-ZAR quote you want to apply.", "OK"); return; }
                skyframeAmount = price * skyframePerZar;
            }
            if (!TryAmount(skyframeAmount.ToString(CultureInfo.InvariantCulture), out var atomic))
            { await DisplayAlert("Price out of range", "The converted SKYF price is out of range.", "OK"); return; }
            _posAmountAtomic = atomic;
            _posCreatedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            _posRequestReference = $"SKYF-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";
            if (atomic < 1_000_000) { await DisplayAlert("Price too small", "The converted amount is below the minimum payment amount.", "OK"); return; }
            var uri = $"skyframe:{_address}?amount={(atomic / 100_000_000m).ToString(CultureInfo.InvariantCulture)}&reference={_posRequestReference}";
            var requestPanel = new VerticalStackLayout { Spacing = 8 };
            requestPanel.Add(Text($"Request {_posAmountAtomic / 100_000_000m:N4} SKYF", MainText, 20, true));
            requestPanel.Add(Text($"Reference: {_posRequestReference}", Accent, 14, true));
            requestPanel.Add(new BarcodeGeneratorView { Value = uri, Format = BarcodeFormat.QrCode, WidthRequest = 240, HeightRequest = 240, BackgroundColor = Colors.White, HorizontalOptions = LayoutOptions.Center });
            _posStatus = Text("Checking five official nodes for payment…", Warning);
            requestPanel.Add(_posStatus);
            layout.Add(Panel(requestPanel));
            StartPaymentPolling(_posStatus);
        }, true);
        layout.Add(create);
    }

    private void StartPaymentPolling(Label status)
    {
        _posTimer?.Stop();
        _posTimer = Dispatcher.CreateTimer();
        _posTimer.Interval = TimeSpan.FromSeconds(3);
        _posTimer.Tick += async (_, _) =>
        {
            if (_address is null) return;
            var result = await _nodes.CheckIncomingPaymentAsync(_address, _posAmountAtomic, _posCreatedAt, _posRequestReference);
            status.Text = result.Status switch
            {
                "confirmed" => $"Confirmed · {result.MatchingNodes}/5 nodes · {result.TxId}",
                "mempool" => $"Payment in mempool · {result.MatchingNodes}/5 nodes",
                "unavailable" => "Configure five official HTTPS gateways to check payments.",
                _ => $"Waiting for payment · {result.MatchingNodes}/5 nodes observe it."
            };
            status.TextColor = result.Status == "confirmed" ? Good : result.Status == "mempool" ? Accent : Warning;
            if (result.Status == "confirmed") _posTimer.Stop();
        };
        _posTimer.Start();
    }

    private void BuildEscrow(VerticalStackLayout layout)
    {
        layout.Add(Text("P2P ESCROW", Accent, 14, true));
        layout.Add(Text("Create an escrow lock, claim using the seller secret, or dispute a lock to pause its expiry timer.", Muted));
        var seller = Input("Seller skyf1 address");
        var arbiter = Input("Arbiter skyf1 address");
        var amount = Input("Amount in SKYF");
        var secret = Input("Seller secret code");
        var hours = Input("Expiry hours (default 24)");
        amount.Keyboard = Keyboard.Numeric; hours.Keyboard = Keyboard.Numeric;
        layout.Add(seller); layout.Add(arbiter); layout.Add(amount); layout.Add(secret); layout.Add(hours);
        layout.Add(ActionButton("Create escrow lock", async () =>
        {
            if (!IsSkyframeAddress(seller.Text) || !IsSkyframeAddress(arbiter.Text) || !TryAmount(amount.Text, out var atomic) || atomic < 1_000_000 || string.IsNullOrWhiteSpace(secret.Text))
            { await DisplayAlert("Escrow fields required", "Enter a valid seller, arbiter, amount, and secret.", "OK"); return; }
            var snapshot = await GetAccountSnapshotAsync(); if (snapshot is null) return;
            var paymentReference = $"SKYF-{Convert.ToHexString(RandomNumberGenerator.GetBytes(3))}";
            var escrowId = Guid.NewGuid().ToString("N");
            var lockRecord = new EscrowRecord
            {
                EscrowId = escrowId, PaymentReference = paymentReference, SellerAddress = seller.Text.Trim(), BuyerAddress = _address!,
                ArbiterAddress = arbiter.Text.Trim(), AmountAtomic = atomic,
                SecretHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret.Text))).ToLowerInvariant(),
                ExpiryTimestampUtc = DateTimeOffset.UtcNow.AddHours(ParseHours(hours.Text)).ToUnixTimeSeconds()
            };
            var transaction = CreateSignedTransaction(TransactionType.EscrowLock, seller.Text.Trim(), atomic, snapshot.Nonce, JsonSerializer.Serialize(lockRecord));
            var result = await _nodes.BroadcastAsync(transaction);
            await DisplayAlert(result.Accepted ? "Escrow submitted" : "Escrow status", $"{paymentReference}\n{result.Message}", "OK");
        }, true));

        layout.Add(Text("CLAIM OR DISPUTE", Accent, 14, true));
        var escrowIdEntry = Input("Escrow ID");
        var claimSecret = Input("Seller secret code");
        layout.Add(escrowIdEntry); layout.Add(claimSecret);
        layout.Add(ActionButton("Claim escrow", async () =>
        {
            if (string.IsNullOrWhiteSpace(escrowIdEntry.Text) || string.IsNullOrWhiteSpace(claimSecret.Text)) { await DisplayAlert("Escrow details required", "Enter the escrow ID and seller secret.", "OK"); return; }
            await SubmitEscrowActionAsync(TransactionType.EscrowClaim, new { escrowId = escrowIdEntry.Text.Trim(), secret = claimSecret.Text });
        }, true));
        layout.Add(ActionButton("Dispute escrow", async () =>
        {
            if (string.IsNullOrWhiteSpace(escrowIdEntry.Text)) { await DisplayAlert("Escrow ID required", "Enter the escrow ID to dispute.", "OK"); return; }
            if (await DisplayAlert("Freeze expiry timer?", "Submit an on-chain dispute transaction for this escrow?", "Submit", "Cancel"))
                await SubmitEscrowActionAsync(TransactionType.EscrowDispute, new { escrowId = escrowIdEntry.Text.Trim() });
        }));
        var escrowList = new VerticalStackLayout { Spacing = 8 };
        layout.Add(Text("Recent network escrow records", Accent, 14, true));
        layout.Add(escrowList);
        layout.Add(ActionButton("Refresh escrows", async () => await LoadEscrowsAsync(escrowList)));
        _ = LoadEscrowsAsync(escrowList);
    }

    private async Task LoadEscrowsAsync(VerticalStackLayout container)
    {
        container.Clear();
        var records = await _nodes.GetEscrowsAsync();
        var relevant = records.Where(e => e.Status == EscrowStatus.Locked
            && (_address == e.BuyerAddress || _address == e.SellerAddress || _address == e.ArbiterAddress)).ToArray();
        if (relevant.Length == 0) { container.Add(Text("No active escrow records found across configured nodes.", Muted)); return; }
        foreach (var escrow in relevant)
        {
            var secondsLeft = escrow.ExpiryTimestampUtc + escrow.FrozenDurationSeconds - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var card = new VerticalStackLayout { Spacing = 4, Padding = 12, BackgroundColor = Surface };
            card.Add(Text($"{escrow.PaymentReference} · {escrow.AmountAtomic / 100_000_000m:N4} SKYF", MainText, 14, true));
            card.Add(Text($"ID {escrow.EscrowId}", Muted, 11));
            card.Add(Text(escrow.IsDisputed ? "Disputed · timer paused" : $"Expires in {TimeSpan.FromSeconds(Math.Max(0, secondsLeft)):g}", escrow.IsDisputed ? Warning : Muted, 12));
            container.Add(Panel(card));
        }
    }

    private void BuildSettings(VerticalStackLayout layout)
    {
        layout.Add(Text("PUBLIC NODE GATEWAYS", Accent, 14, true));
        layout.Add(Text("Use five independent HTTPS gateways for 5-of-5 public network consensus. The required matching responses scale from one to five as gateways are added. For local Windows desktop testing, enter only http://127.0.0.1:6001; this loopback-only mode checks one local node and is not decentralized consensus.", Muted));
        _nodeEditor = new Editor { Text = _nodes.GetSavedNodes(), Placeholder = "http://127.0.0.1:6001", AutoSize = EditorAutoSizeOption.TextChanges, MinimumHeightRequest = 130, TextColor = MainText, PlaceholderColor = Muted, BackgroundColor = Surface };
        layout.Add(_nodeEditor);
        layout.Add(ActionButton("Save gateway list", async () =>
        {
            try { _nodes.SaveNodes(_nodeEditor.Text ?? ""); await DisplayAlert("Saved", "Node gateways are cached on this device.", "OK"); }
            catch (Exception ex) { await DisplayAlert("Invalid gateway", ex.Message, "OK"); }
        }, true));
        layout.Add(Text($"Required network identity:\n{Skyframe.Ledger.Core.NetworkConstants.OfficialGenesisHash}", Muted, 11));
        layout.Add(ActionButton("Lock wallet", async () =>
        {
            if (await DisplayAlert("Lock wallet?", "Remove the stored recovery phrase from secure storage?", "Lock", "Cancel"))
            { await _vault.ClearAsync(); _mnemonic = null; _address = null; ShowOnboarding(); }
        }));
    }

    private async Task ScanQrAsync()
    {
        var permission = await Permissions.RequestAsync<Permissions.Camera>();
        if (permission != PermissionStatus.Granted) { await DisplayAlert("Camera permission", "Camera access is needed to scan Skyframe payment QR codes.", "OK"); return; }
        var scanner = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions { Formats = BarcodeFormats.TwoDimensional, AutoRotate = true, Multiple = false },
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Fill
        };
        scanner.BarcodesDetected += (_, args) =>
        {
            var value = args.Results.FirstOrDefault()?.Value;
            if (string.IsNullOrWhiteSpace(value)) return;
            MainThread.BeginInvokeOnMainThread(() =>
            {
                scanner.IsDetecting = false;
                Content = new VerticalStackLayout { Padding = 20, BackgroundColor = PageBackground, Children = { Text("QR scanned", Accent, 20, true), Text(value, MainText), ActionButton("Back to wallet", () => { _section = "Wallet"; ShowApp(); }) } };
                ParsePaymentQr(value);
            });
        };
        Content = new Grid { BackgroundColor = PageBackground, Children = { scanner, new VerticalStackLayout { Padding = 16, VerticalOptions = LayoutOptions.End, Children = { ActionButton("Cancel scan", () => ShowApp()) } } } };
    }

    private void ParsePaymentQr(string value)
    {
        if (!value.StartsWith("skyframe:", StringComparison.OrdinalIgnoreCase)) return;
        var parts = value[5..].Split('?', 2);
        var address = parts[0];
        if (IsSkyframeAddress(address)) { _scannedAddress = address; if (_destination is not null) _destination.Text = address; }
        var amount = parts.Length < 2 ? null : parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2 && pair[0] == "amount")
            .Select(pair => Uri.UnescapeDataString(pair[1])).FirstOrDefault();
        _scannedReference = parts.Length < 2 ? null : parts[1].Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2)).Where(pair => pair.Length == 2 && pair[0] == "reference")
            .Select(pair => Uri.UnescapeDataString(pair[1])).FirstOrDefault();
        if (amount is not null) { _scannedAmount = amount; if (_sendAmount is not null) _sendAmount.Text = amount; }
    }

    private async Task<AccountSnapshot?> GetAccountSnapshotAsync()
    {
        if (_address is null) return null;
        var result = await _nodes.GetBalanceAsync(_address);
        if (!result.Success || result.Snapshot is null) { await DisplayAlert("Account quorum unavailable", result.Message, "OK"); return null; }
        if (!string.Equals(_nonceAddress, _address, StringComparison.Ordinal))
        {
            _nonceAddress = _address;
            _hasLocalNonce = false;
        }
        if (!_hasLocalNonce || result.Snapshot.Nonce > _nextLocalNonce) _nextLocalNonce = result.Snapshot.Nonce;
        _hasLocalNonce = true;
        return result.Snapshot with { Nonce = _nextLocalNonce };
    }

    private async Task SubmitEscrowActionAsync(TransactionType type, object payload)
    {
        var snapshot = await GetAccountSnapshotAsync(); if (snapshot is null) return;
        var transaction = CreateSignedTransaction(type, "", 0, snapshot.Nonce, JsonSerializer.Serialize(payload));
        var result = await _nodes.BroadcastAsync(transaction);
        await DisplayAlert(result.Accepted ? "Escrow transaction submitted" : "Escrow status", result.Message, "OK");
    }

    private Transaction CreateSignedTransaction(TransactionType type, string toAddress, long amountAtomic, long nonce, string payload)
    {
        if (string.IsNullOrWhiteSpace(_mnemonic)) throw new InvalidOperationException("Wallet is locked.");
        using var wallet = WalletHelper.DeriveKeypair(_mnemonic);
        var tx = new Transaction
        {
            TxId = Guid.NewGuid().ToString("N"), Type = type, Nonce = nonce,
            FromAddress = wallet.PublicAddress, ToAddress = toAddress,
            AmountAtomic = amountAtomic, FeeAtomic = 100_000,
            TimestampUtc = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), PayloadJson = payload
        };
        tx.Signature = Convert.ToHexString(wallet.Sign(Encoding.UTF8.GetBytes(tx.GetSigningPayload()))).ToLowerInvariant();
        _nextLocalNonce = Math.Max(_nextLocalNonce, nonce + 1);
        _nonceAddress = wallet.PublicAddress;
        _hasLocalNonce = true;
        return tx;
    }

    private static bool TryAmount(string? input, out long amountAtomic)
    {
        amountAtomic = 0;
        if (!decimal.TryParse(input, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount) || amount < 0) return false;
        try { amountAtomic = decimal.ToInt64(decimal.Round(amount * 100_000_000m, 0, MidpointRounding.AwayFromZero)); return true; }
        catch (OverflowException) { return false; }
    }

    private static string ShortAddress(string? address) => string.IsNullOrWhiteSpace(address) ? "unknown" : address.Length > 24 ? address[..12] + "…" + address[^8..] : address;

    private static bool IsSkyframeAddress(string? address) => address is { Length: 45 }
        && address.StartsWith("skyf1", StringComparison.Ordinal)
        && address.AsSpan(5).IndexOfAnyExcept("0123456789abcdef") < 0;

    private static int ParseHours(string? input) => int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours) ? Math.Clamp(hours, 1, 8760) : 24;
    private VerticalStackLayout PageLayout() => new() { Padding = new Thickness(20, 28), Spacing = 15, BackgroundColor = PageBackground };
    private static VerticalStackLayout Heading(string eyebrow, string title) => new() { Spacing = 5, Children = { Text(eyebrow, Accent, 12, true), Text(title, MainText, 27, true) } };
    private static Label Text(string text, Color color, double size = 14, bool bold = false) => new() { Text = text, TextColor = color, FontSize = size, FontAttributes = bold ? FontAttributes.Bold : FontAttributes.None, LineBreakMode = LineBreakMode.WordWrap };
    private static Entry Input(string placeholder) => new() { Placeholder = placeholder, TextColor = MainText, PlaceholderColor = Muted, BackgroundColor = Surface, ClearButtonVisibility = ClearButtonVisibility.WhileEditing };
    private static Label Chip(string text, Color color) => new() { Text = text, TextColor = color, BackgroundColor = Surface, Padding = new Thickness(9, 5), Margin = new Thickness(2), FontSize = 13 };
    private static Border Panel(View content) => new() { Content = content, BackgroundColor = Surface, Padding = 16, Stroke = Color.FromArgb("#383A40"), StrokeThickness = 1, StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 } };
    private static Button ActionButton(string text, Action action, bool primary = false) => new()
    {
        Text = text, TextColor = primary ? PageBackground : MainText, BackgroundColor = primary ? Accent : Surface,
        CornerRadius = 10, Padding = new Thickness(14, 10), FontAttributes = FontAttributes.Bold,
        Command = new Command(action)
    };
    private static Button ActionButton(string text, Func<Task> action, bool primary = false) => new()
    {
        Text = text, TextColor = primary ? PageBackground : MainText, BackgroundColor = primary ? Accent : Surface,
        CornerRadius = 10, Padding = new Thickness(14, 10), FontAttributes = FontAttributes.Bold,
        Command = new Command(async () => await action())
    };
}
