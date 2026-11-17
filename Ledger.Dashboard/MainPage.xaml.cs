using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skyframe.Ledger.Core.Models;
using Skyframe.Ledger.Core.Wallet;
using Skyframe.Ledger.Core.Security;
using Microsoft.Maui.Layouts;
using Microsoft.Maui.Storage;

namespace Ledger.Dashboard;

public partial class MainPage : ContentPage
{
    private static readonly Color Bg=Color.FromArgb("#090A0C"), Surface=Color.FromArgb("#151619"), Raised=Color.FromArgb("#202125"), Stroke=Color.FromArgb("#383A40"), Cyan=Color.FromArgb("#FFFFFF"), Ink=Color.FromArgb("#F4F4F5"), Muted=Color.FromArgb("#A1A1AA"), Green=Color.FromArgb("#34D399"), Amber=Color.FromArgb("#FBBF24"), Red=Color.FromArgb("#FB7185");
    private readonly NodeApiClient _api=new();
    private readonly Dictionary<string,long> _nonces=new(StringComparer.Ordinal);
    private string _dataDir=GetConfiguredDataDirectory();
    private int _port=Preferences.Get("skyframe-node-port",Preferences.Get("kora-node-port",5001)),_refreshing,_logOffset;
    private string? _mnemonic,_address,_newPhrase;
    private string _importText="",_verifyA="",_verifyB="",_logBuffer="";
    private int[] _verifyIndices=[];
    private bool _initialized,_importValid,_networkKnown,_testnet;
    private Label? _status,_network,_height,_peers,_balance,_uptime,_eligibility,_addressLabel,_importStatus;
    private Entry? _filter,_portInput;
    private Button? _saveNew,_saveImport;
    private FlexLayout? _chips,_suggestions;
    private VerticalStackLayout? _logRows,_escrows;

    public MainPage(){InitializeComponent();BackgroundColor=Bg;_api.NodePort=_port;}
    protected override async void OnAppearing(){base.OnAppearing();if(_initialized)return;_initialized=true;await OpenInitialAsync();}

    private async Task OpenInitialAsync()
    {
        try
        {
            _dataDir = Path.GetFullPath(_dataDir);
            if (NodeProcessLauncher.IsInstalled)
                await MigrateInstalledDataAsync(_dataDir);
            Directory.CreateDirectory(_dataDir);
            var identityPath = Path.Combine(_dataDir, "node_identity.json");
            var legacyPath = Path.Combine(_dataDir, "wallet_config.json");
            if (!File.Exists(identityPath) && !File.Exists(legacyPath)) { ShowOnboarding(); return; }

            var identityJson = File.Exists(identityPath)
                ? MachineBoundDataProtection.LoadIdentity(_dataDir)
                : await File.ReadAllTextAsync(legacyPath);
            using var doc = JsonDocument.Parse(identityJson);
            _mnemonic = ReadString(doc.RootElement, "mnemonic") ?? ReadString(doc.RootElement, "Mnemonic");
            if (string.IsNullOrWhiteSpace(_mnemonic)) throw new InvalidDataException("Wallet identity is missing its mnemonic field.");
            if (!File.Exists(identityPath))
            {
                MachineBoundDataProtection.SaveIdentity(_dataDir, identityJson);
                File.Delete(legacyPath);
            }
            using var wallet = WalletHelper.DeriveKeypair(_mnemonic);
            _address = wallet.PublicAddress;
            ShowDashboard();
        }
        catch (Exception ex) { await DisplayAlert("Wallet setup", ex.Message, "OK"); ShowOnboarding(); }
    }

    private static async Task MigrateInstalledDataAsync(string destination)
    {
        if (File.Exists(Path.Combine(destination, "node_identity.json")) || File.Exists(Path.Combine(destination, "wallet_config.json"))) return;

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preferred = Preferences.Get("skyframe-data-dir", string.Empty);
        if (!string.IsNullOrWhiteSpace(preferred)) candidates.Add(Path.GetFullPath(preferred));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyframe", "data"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KoraNetwork", "data"));
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            candidates.Add(Path.Combine(directory.FullName, "Skyframe", "data"));
            candidates.Add(Path.Combine(directory.FullName, "data"));
        }

        var source = candidates.FirstOrDefault(path => !string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)
            && (File.Exists(Path.Combine(path, "node_identity.json")) || File.Exists(Path.Combine(path, "wallet_config.json"))));
        if (source is null) return;

        Directory.CreateDirectory(destination);
        var machineKey = Path.Combine(source, "node_machine_key.dpapi");
        if (File.Exists(machineKey)) File.Copy(machineKey, Path.Combine(destination, "node_machine_key.dpapi"), true);
        foreach (var name in new[] { "ledger.json", "peers.json", "node.log" })
        {
            var from = Path.Combine(source, name);
            if (File.Exists(from)) File.Copy(from, Path.Combine(destination, name), true);
        }

        var encryptedIdentity = Path.Combine(source, "node_identity.json");
        if (File.Exists(encryptedIdentity)) File.Copy(encryptedIdentity, Path.Combine(destination, "node_identity.json"), true);
        else
        {
            var legacy = await File.ReadAllTextAsync(Path.Combine(source, "wallet_config.json"));
            MachineBoundDataProtection.SaveIdentity(destination, legacy);
        }

        // Verify machine-bound key material before switching the installed Dashboard to ProgramData.
        _ = MachineBoundDataProtection.LoadIdentity(destination);
        var legacyWallet = Path.Combine(source, "wallet_config.json");
        if (File.Exists(legacyWallet)) File.Delete(legacyWallet);
        Preferences.Set("skyframe-data-dir", destination);
    }
    private void ShowOnboarding(bool import=false)
    {
        var body=new VerticalStackLayout{Spacing=16,Padding=new Thickness(36,28),WidthRequest=920,HorizontalOptions=LayoutOptions.Center};
        body.Add(new Image { Source = "skyframe_logo.svg", WidthRequest = 190, HeightRequest = 190, Aspect = Aspect.AspectFit, HorizontalOptions = LayoutOptions.Center });
        body.Add(T("SKYFRAME NETWORK  /  WALLET SETUP",12,true,Cyan));body.Add(T("Your wallet starts here.",32,true));body.Add(T("Create a recovery phrase or import one to connect this PC to your wallet.",14,false,Muted));
        var settings=new VerticalStackLayout{Spacing=7};settings.Add(T("Node data directory",13,true));var path=E("Directory containing node_identity.json",_dataDir);path.TextChanged+=(_,e)=>{if(!string.IsNullOrWhiteSpace(e.NewTextValue))_dataDir=e.NewTextValue.Trim();};settings.Add(path);settings.Add(T("Run Ledger.Node with this same --data-dir. Skyframe uses a fresh data directory for the new network.",12,false,Muted));
        var portRow=new HorizontalStackLayout{Spacing=10};portRow.Add(T("Node P2P port",13,true));var port=E("5001",_port.ToString(CultureInfo.InvariantCulture));port.WidthRequest=100;port.Keyboard=Keyboard.Numeric;port.TextChanged+=(_,e)=>{if(int.TryParse(e.NewTextValue,out var n)&&n is >0 and <=64535)_port=n;};portRow.Add(port);settings.Add(portRow);body.Add(Panel(settings));
        var tabs=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10};tabs.Add(B("Create New Wallet",()=>ShowOnboarding(false),!import),0,0);tabs.Add(B("Import Existing Wallet",()=>ShowOnboarding(true),import),1,0);body.Add(tabs);body.Add(import?BuildImport():BuildCreate());
        Content=new ScrollView{Content=body,BackgroundColor=Bg};
    }

    private View BuildCreate()
    {
        var box=new VerticalStackLayout{Spacing=14};box.Add(T("Create a new wallet",20,true));box.Add(T("Your 12 words are the only recovery method for this wallet. Keep them private and offline.",13,false,Muted));box.Add(B(_newPhrase is null?"Generate recovery phrase":"Generate another phrase",Generate,true));
        if(_newPhrase is null)return Panel(box);
        var words=_newPhrase.Split(' ',StringSplitOptions.RemoveEmptyEntries);var grid=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(GridLength.Star),new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=7,RowSpacing=7};
        for(var i=0;i<words.Length;i++){var row=i/3;if(grid.RowDefinitions.Count<=row)grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));var item=new HorizontalStackLayout{Spacing=8,Padding=new Thickness(12,9),BackgroundColor=Raised};item.Add(T((i+1).ToString("00"),12,true,Muted));item.Add(T(words[i],14,true));grid.Add(item,i%3,row);}box.Add(grid);box.Add(T("Verify two randomly selected words",14,true));
        var verify=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=10};var a=E($"Word #{_verifyIndices[0]+1}",_verifyA);var b=E($"Word #{_verifyIndices[1]+1}",_verifyB);a.TextChanged+=(_,e)=>{_verifyA=e.NewTextValue??"";UpdateCreate();};b.TextChanged+=(_,e)=>{_verifyB=e.NewTextValue??"";UpdateCreate();};verify.Add(a,0,0);verify.Add(b,1,0);box.Add(verify);
        _saveNew=B("Save wallet and open dashboard",()=>_=SaveWalletAsync(_newPhrase),true);_saveNew.IsEnabled=PhraseVerified();box.Add(_saveNew);return Panel(box);
    }
    private void Generate(){_newPhrase=WalletHelper.GenerateNewMnemonic();_verifyIndices=Enumerable.Range(0,12).OrderBy(_=>RandomNumberGenerator.GetInt32(int.MaxValue)).Take(2).Order().ToArray();_verifyA=_verifyB="";ShowOnboarding();}
    private bool PhraseVerified(){if(_newPhrase is null||_verifyIndices.Length!=2)return false;var w=_newPhrase.Split(' ',StringSplitOptions.RemoveEmptyEntries);return _verifyA.Trim().Equals(w[_verifyIndices[0]],StringComparison.OrdinalIgnoreCase)&&_verifyB.Trim().Equals(w[_verifyIndices[1]],StringComparison.OrdinalIgnoreCase);}
    private void UpdateCreate(){if(_saveNew is not null)_saveNew.IsEnabled=PhraseVerified();}

    private View BuildImport()
    {
        var box=new VerticalStackLayout{Spacing=11};box.Add(T("Import an existing wallet",20,true));box.Add(T("Enter the 12-word English recovery phrase. Suggestions use the BIP-39 list; validation runs locally.",13,false,Muted));
        var editor=new Editor{Text=_importText,AutoSize=EditorAutoSizeOption.TextChanges,MinimumHeightRequest=130,FontSize=15,TextColor=Ink,Placeholder="Enter your recovery words…",PlaceholderColor=Muted,BackgroundColor=Raised};editor.TextChanged+=(_,e)=>{_importText=e.NewTextValue??"";UpdateImport();};box.Add(editor);
        _chips=new FlexLayout{Wrap=FlexWrap.Wrap,Direction=FlexDirection.Row};_suggestions=new FlexLayout{Wrap=FlexWrap.Wrap,Direction=FlexDirection.Row};box.Add(_chips);box.Add(_suggestions);_importStatus=T("Enter 12 words to validate the recovery phrase.",13,false,Muted);box.Add(_importStatus);_saveImport=B("Import wallet and open dashboard",()=>_=SaveWalletAsync(_importText),true);_saveImport.IsEnabled=false;box.Add(_saveImport);UpdateImport();return Panel(box);
    }
    private void UpdateImport()
    {
        if(_chips is null||_suggestions is null)return;_chips.Children.Clear();_suggestions.Children.Clear();var words=_importText.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries).Select(x=>x.ToLowerInvariant()).ToArray();
        foreach(var word in words){var valid=WalletHelper.GetEnglishWordSuggestions(word,2048).Contains(word,StringComparer.Ordinal);_chips.Children.Add(Chip(word,valid?Green:Red));}
        var current=_importText.Length>0&&char.IsWhiteSpace(_importText[^1])?"":words.LastOrDefault()??"";
        if(current.Length>0)foreach(var candidate in WalletHelper.GetEnglishWordSuggestions(current,8).Where(w=>w!=current)){var button=new Button{Text=candidate,FontSize=12,TextColor=Cyan,BackgroundColor=Raised,CornerRadius=8,Padding=new Thickness(10,6),Margin=new Thickness(2)};button.Clicked+=(_,_)=>ApplySuggestion(candidate);_suggestions.Children.Add(button);}
        _importValid=false;var msg=$"{words.Length}/12 words";var color=Muted;if(words.Length==12){try{_=WalletHelper.NormalizeMnemonic(_importText);_importValid=true;msg="Checksum valid — ready to import.";color=Green;}catch(Exception ex){msg=ex.Message;color=Red;}}else if(words.Length>12){msg="A BIP-39 phrase must contain exactly 12 words.";color=Red;}
        _importStatus!.Text=msg;_importStatus.TextColor=color;_saveImport!.IsEnabled=_importValid;
    }
    private void ApplySuggestion(string word){var end=_importText.Length;while(end>0&&char.IsWhiteSpace(_importText[end-1]))end--;var start=end;while(start>0&&!char.IsWhiteSpace(_importText[start-1]))start--;_importText=_importText[..start]+word+" ";ShowOnboarding(true);}

    private async Task SaveWalletAsync(string? input)
    {
        try
        {
            var phrase = WalletHelper.NormalizeMnemonic(input ?? "");
            _dataDir = Path.GetFullPath(_dataDir);
            Directory.CreateDirectory(_dataDir);
            using var key = WalletHelper.DeriveKeypair(phrase);
            var config = new { mnemonic = phrase, address = key.PublicAddress, publicKeyHex = Convert.ToHexString(key.PublicKey).ToLowerInvariant() };
            var identityJson = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            MachineBoundDataProtection.SaveIdentity(_dataDir, identityJson);
            var legacyPath = Path.Combine(_dataDir, "wallet_config.json");
            if (File.Exists(legacyPath)) File.Delete(legacyPath);
            Preferences.Set("skyframe-data-dir", _dataDir);
            Preferences.Set("skyframe-node-port", _port);
            _api.NodePort = _port;
            _mnemonic = phrase;
            _address = key.PublicAddress;
            ShowDashboard();
            await DisplayAlert("Encrypted wallet saved", $"Machine-bound encrypted identity saved to:\n{Path.Combine(_dataDir, "node_identity.json")}\n\nKeep the recovery phrase private.", "Continue");
        }
        catch (Exception ex) { await DisplayAlert("Wallet not saved", ex.Message, "OK"); }
    }
    private void ShowDashboard()
    {
        _api.NodePort=_port;
        const double dashboardMaxWidth = 1400;
        var body=new VerticalStackLayout{Spacing=16,Padding=new Thickness(28,22),MaximumWidthRequest=dashboardMaxWidth,HorizontalOptions=LayoutOptions.Center};
        var viewport=new Grid{HorizontalOptions=LayoutOptions.Fill};
        viewport.Add(body);
        body.Add(Header());
        var recoveryActions=new HorizontalStackLayout{Spacing=8};
        recoveryActions.Add(B("Reveal recovery phrase",()=>_=RevealRecoveryPhrase()));
        recoveryActions.Add(B("Copy recovery phrase",()=>_=CopyRecoveryPhrase()));
        body.Add(recoveryActions);
        var metrics=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(GridLength.Star),new(GridLength.Star),new(GridLength.Star),new(GridLength.Star)},ColumnSpacing=12};metrics.Add(Metric("SYNC STATUS","Block height",out _height),0,0);metrics.Add(Metric("CONNECTED PEERS","Outbound / inbound",out _peers),1,0);metrics.Add(Metric("WALLET BALANCE","SKYF",out _balance),2,0);
        var uptimeCard=Metric("NODE UPTIME","Time since node start",out _uptime);_eligibility=T("Checking eligibility…",12,false,Muted);((VerticalStackLayout)((Border)uptimeCard).Content!).Add(_eligibility);metrics.Add(uptimeCard,3,0);body.Add(metrics);
        var split=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(new GridLength(1.15,GridUnitType.Star)),new(new GridLength(.85,GridUnitType.Star))},ColumnSpacing=14};split.Add(LogPanel(),0,0);split.Add(EscrowPanel(),1,0);body.Add(split);
        var scroll=new ScrollView{Content=viewport,BackgroundColor=Bg,HorizontalOptions=LayoutOptions.Fill};
        scroll.SizeChanged+=(_,_)=>
        {
            if(scroll.Width<=0)return;
            if(Math.Abs(viewport.WidthRequest-scroll.Width)>0.5)viewport.WidthRequest=scroll.Width;
            var contentWidth=Math.Min(dashboardMaxWidth,scroll.Width);
            if(Math.Abs(body.WidthRequest-contentWidth)>0.5)body.WidthRequest=contentWidth;
        };
        Content=scroll;_=Refresh();Dispatcher.StartTimer(TimeSpan.FromSeconds(2),()=>{_=Refresh();return true;});
    }
    private async Task RevealRecoveryPhrase()
    {
        if (string.IsNullOrWhiteSpace(_mnemonic))
        {
            await DisplayAlert("Phrase unavailable", "The Dashboard has no loaded wallet phrase.", "OK");
            return;
        }

        var address = _address ?? "unknown wallet";
        var confirmed = await DisplayAlert(
            "Reveal recovery phrase?",
            $"This phrase controls {address}. Anyone who sees it can control the wallet. Show it only when you are somewhere private.",
            "Reveal",
            "Cancel");
        if (confirmed)
            await DisplayAlert("Recovery phrase", _mnemonic, "Close");
    }

    private async Task CopyRecoveryPhrase()
    {
        if (string.IsNullOrWhiteSpace(_mnemonic))
        {
            await DisplayAlert("Phrase unavailable", "The Dashboard has no loaded wallet phrase.", "OK");
            return;
        }

        var confirmed = await DisplayAlert(
            "Copy recovery phrase?",
            $"Copy the phrase for {(_address ?? "unknown wallet")} to the system clipboard? Anyone with clipboard access can read it.",
            "Copy",
            "Cancel");
        if (!confirmed) return;

        await Clipboard.Default.SetTextAsync(_mnemonic);
        await DisplayAlert("Copied", "Recovery phrase copied. Paste it only into the intended wallet import screen, then clear the clipboard.", "OK");
    }

    private View Header()
    {
        var grid=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=12};var title=new HorizontalStackLayout{Spacing=12,VerticalOptions=LayoutOptions.Center};title.Add(new Image{Source="skyframe_logo.svg",WidthRequest=48,HeightRequest=48,Aspect=Aspect.AspectFit});var titleText=new VerticalStackLayout{Spacing=2,VerticalOptions=LayoutOptions.Center};titleText.Add(T("SKYFRAME NETWORK",12,true,Cyan));titleText.Add(T("Skyframe Dashboard",25,true));title.Add(titleText);grid.Add(title,0,0);
        var badges=new HorizontalStackLayout{Spacing=8,VerticalOptions=LayoutOptions.Center};_status=Badge("STANDBY",Amber);_network=Badge("OFFLINE",Muted);badges.Add(_status);badges.Add(_network);grid.Add(badges,1,0);
        var buttons=new HorizontalStackLayout{Spacing=6,VerticalOptions=LayoutOptions.Center};_portInput=E("5001",_port.ToString(CultureInfo.InvariantCulture));_portInput.WidthRequest=75;_portInput.Keyboard=Keyboard.Numeric;_portInput.TextChanged+=(_,e)=>{if(int.TryParse(e.NewTextValue,out var n)&&n is >0 and <=64535){_port=n;_api.NodePort=n;Preferences.Set("skyframe-node-port",n);}};buttons.Add(_portInput);buttons.Add(B("Start Node Service",()=>_=Control("start"),true));buttons.Add(B("Stop",()=>_=Control("stop")));buttons.Add(B("Restart",()=>_=Control("restart")));grid.Add(buttons,2,0);return grid;
    }
    private View Metric(string title,string caption,out Label value){var box=new VerticalStackLayout{Spacing=8};box.Add(T(title,11,true,Muted));value=T("—",23,true);box.Add(value);box.Add(T(caption,12,false,Muted));if(title=="WALLET BALANCE"){var row=new HorizontalStackLayout{Spacing=4};_addressLabel=T(ShortAddress(_address),11,false,Cyan);row.Add(_addressLabel);var copy=new Button{Text="Copy",FontSize=11,TextColor=Cyan,BackgroundColor=Colors.Transparent,Padding=new Thickness(5,1)};copy.Clicked+=async(_,_)=>{if(_address is not null)await Clipboard.Default.SetTextAsync(_address);};row.Add(copy);box.Add(row);}return Panel(box);}

    private View LogPanel(){var box=new VerticalStackLayout{Spacing=9};var header=new Grid{ColumnDefinitions=new ColumnDefinitionCollection{new(GridLength.Star),new(GridLength.Auto),new(GridLength.Auto)},ColumnSpacing=7};header.Add(T("Node activity",17,true),0,0);_filter=E("Filter log","");_filter.WidthRequest=180;_filter.TextChanged+=(_,_)=>RenderLogs();header.Add(_filter,1,0);var clear=new Button{Text="Clear",TextColor=Muted,BackgroundColor=Raised,CornerRadius=8,Padding=new Thickness(12,6)};clear.Clicked+=async(_,_)=>{var file=Path.Combine(_dataDir,"node.log");if(File.Exists(file))_logOffset=(await File.ReadAllTextAsync(file)).Length;_logBuffer="";RenderLogs();};header.Add(clear,2,0);box.Add(header);_logRows=new VerticalStackLayout{Spacing=5};box.Add(new ScrollView{HeightRequest=300,Content=_logRows});return Panel(box);}
    private void RenderLogs(){if(_logRows is null)return;_logRows.Children.Clear();var f=_filter?.Text?.Trim()??"";var lines=_logBuffer.Split(new[]{"\r\n","\n"},StringSplitOptions.RemoveEmptyEntries).Where(x=>f.Length==0||x.Contains(f,StringComparison.OrdinalIgnoreCase)).TakeLast(160);foreach(var line in lines)_logRows.Add(T(line,11,false,Muted));if(!_logRows.Children.Any())_logRows.Add(T("Waiting for node log entries…",12,false,Muted));}
    private View EscrowPanel(){var box=new VerticalStackLayout{Spacing=9};box.Add(T("Active P2P escrow locks",17,true));box.Add(T("Payment references, timers and settlement actions",12,false,Muted));_escrows=new VerticalStackLayout{Spacing=9};box.Add(new ScrollView{HeightRequest=340,Content=_escrows});return Panel(box);}

    private async Task Control(string action)
    {
        var result=await _api.PostAsync($"control/{action}");if(action=="start"&&!result.Success&&result.Message.Contains("not reachable",StringComparison.OrdinalIgnoreCase))
        {if(!NodeProcessLauncher.TryStart(_port,_dataDir,out var error)){await DisplayAlert("Could not start Ledger.Node",error,"OK");return;}for(var i=0;i<30;i++){await Task.Delay(1000);using var status=await _api.GetJsonAsync("status");if(status is null)continue;result=await _api.PostAsync("control/start");break;}if(!result.Success&&result.Message.Contains("not reachable",StringComparison.OrdinalIgnoreCase))result=(false,"Ledger.Node was launched, but its IPC API did not become available within 30 seconds.");}
        await DisplayAlert(result.Success?"Node control":"Node unavailable",result.Message,"OK");await Refresh();
    }
    private async Task Refresh()
    {
        if(Interlocked.Exchange(ref _refreshing,1)!=0)return;try
        {
            using var status=await _api.GetJsonAsync("status");if(status is null){Set(_status,"STOPPED",Red);Set(_network,_networkKnown?(_testnet?"TESTNET":"MAINNET"):"OFFLINE",_networkKnown?(_testnet?Amber:Cyan):Muted);}
            else{var root=status.RootElement;var running=Str(root,"status")=="running";Set(_status,running?"RUNNING":"STANDBY",running?Green:Amber);_testnet=root.TryGetProperty("testnet",out var t)&&t.ValueKind==JsonValueKind.True;_networkKnown=true;Set(_network,_testnet?"TESTNET":"MAINNET",_testnet?Amber:Cyan);_height!.Text=$"#{Num(root,"blockHeight"):N0}";_peers!.Text=$"{Num(root,"outboundPeers")} / {Num(root,"inboundPeers")}";_uptime!.Text=Format(Num(root,"uptimeSeconds"));var eligible=root.TryGetProperty("rewardEligible",out var e)&&e.GetBoolean();_eligibility!.Text=eligible?"Eligible to propose":"Not yet eligible";_eligibility.TextColor=eligible?Green:Muted;}
            if(_address is not null){using var balance=await _api.GetJsonAsync($"balance/{Uri.EscapeDataString(_address)}");if(balance is not null){var root=balance.RootElement;var amount=root.TryGetProperty("balanceSkyf",out var b)?b.GetDecimal():0m;_balance!.Text=$"{amount:N4} SKYF";if(root.TryGetProperty("nextNonce",out var n))_nonces[_address]=Math.Max(_nonces.GetValueOrDefault(_address),n.GetInt64());}_addressLabel!.Text=ShortAddress(_address);}
            await ReadLog();await ReadEscrows();
        }finally{Interlocked.Exchange(ref _refreshing,0);}
    }
    private async Task ReadLog(){if(_logRows is null)return;try{var path=Path.Combine(_dataDir,"node.log");if(!File.Exists(path)){RenderLogs();return;}var text=await File.ReadAllTextAsync(path);if(text.Length<_logOffset)_logOffset=0;if(text.Length>_logOffset){_logBuffer+=text[_logOffset..];_logOffset=text.Length;if(_logBuffer.Length>200000)_logBuffer=string.Join(Environment.NewLine,_logBuffer.Split(new[]{"\r\n","\n"},StringSplitOptions.RemoveEmptyEntries).TakeLast(1000));}RenderLogs();}catch(IOException){}}
    private async Task ReadEscrows(){if(_escrows is null)return;_escrows.Children.Clear();var records=(await _api.GetEscrowsAsync()).Where(e=>e.Status==EscrowStatus.Locked).ToList();if(records.Count==0){_escrows.Add(T("No active escrow locks.",12,false,Muted));return;}foreach(var record in records)_escrows.Add(EscrowCard(record));}
    private View EscrowCard(EscrowRecord e)
    {
        var box=new VerticalStackLayout{Spacing=5,Padding=new Thickness(11),BackgroundColor=Raised};box.Add(T(e.PaymentReference,13,true,Cyan));box.Add(T($"{e.AmountAtomic/100_000_000m:N4} SKYF · {e.Status}",13,true));var left=e.ExpiryTimestampUtc+e.FrozenDurationSeconds-DateTimeOffset.UtcNow.ToUnixTimeSeconds();box.Add(T(e.IsDisputed?"Expiry timer paused during dispute":left<=0?"Expired · buyer may refund":$"Expires in {Format(left)}",11,false,e.IsDisputed?Amber:Muted));box.Add(T($"Buyer {ShortAddress(e.BuyerAddress)} · Seller {ShortAddress(e.SellerAddress)}",10,false,Muted));var actions=new FlexLayout{Wrap=FlexWrap.Wrap,Direction=FlexDirection.Row};
        if(_address==e.SellerAddress&&left>0&&!e.IsDisputed)actions.Children.Add(B("Claim",()=>_=Claim(e),true));
        if((_address==e.BuyerAddress||_address==e.SellerAddress)&&!e.IsDisputed&&left>0)actions.Children.Add(B("Dispute",()=>_=SubmitEscrow(e,TransactionType.EscrowDispute,new{escrowId=e.EscrowId},"Dispute submitted")));
        if(_address==e.BuyerAddress&&left<=0&&!e.IsDisputed)actions.Children.Add(B("Refund",()=>_=SubmitEscrow(e,TransactionType.EscrowClaim,new{escrowId=e.EscrowId},"Refund requested")));
        if(_address==e.ArbiterAddress&&e.IsDisputed){actions.Children.Add(B("Resolve → seller",()=>_=SubmitEscrow(e,TransactionType.EscrowResolve,new{escrowId=e.EscrowId,recipient=e.SellerAddress},"Resolution submitted")));actions.Children.Add(B("Resolve → buyer",()=>_=SubmitEscrow(e,TransactionType.EscrowResolve,new{escrowId=e.EscrowId,recipient=e.BuyerAddress},"Resolution submitted")));}
        if(actions.Children.Count>0)box.Add(actions);return Panel(box,new Thickness(1));
    }
    private async Task Claim(EscrowRecord e){var secret=await DisplayPromptAsync("Claim escrow","Enter the secret for this escrow.","Submit","Cancel","Secret",-1,Keyboard.Text);if(!string.IsNullOrEmpty(secret))await SubmitEscrow(e,TransactionType.EscrowClaim,new{escrowId=e.EscrowId,secret},"Claim submitted");}
    private async Task SubmitEscrow(EscrowRecord escrow,TransactionType type,object payload,string success)
    {
        if(string.IsNullOrWhiteSpace(_mnemonic)||string.IsNullOrWhiteSpace(_address))return;using var balance=await _api.GetJsonAsync($"balance/{Uri.EscapeDataString(_address)}");if(balance is null){await DisplayAlert("Node unavailable","Could not read the account nonce.","OK");return;}
        var remote=balance.RootElement.TryGetProperty("nextNonce",out var value)?value.GetInt64():0;var nonce=Math.Max(remote,_nonces.GetValueOrDefault(_address));var tx=new Transaction{TxId=Guid.NewGuid().ToString("N"),Type=type,Nonce=nonce,FromAddress=_address,AmountAtomic=0,FeeAtomic=100_000,TimestampUtc=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),PayloadJson=JsonSerializer.Serialize(payload)};
        try{using var wallet=WalletHelper.DeriveKeypair(_mnemonic);tx.Signature=Convert.ToHexString(wallet.Sign(Encoding.UTF8.GetBytes(tx.GetSigningPayload()))).ToLowerInvariant();var result=await _api.PostAsync("tx",tx);if(result.Success)_nonces[_address]=nonce+1;await DisplayAlert(result.Success?success:"Transaction not queued",result.Message,"OK");}catch(Exception ex){await DisplayAlert("Could not sign transaction",ex.Message,"OK");}
    }

    private static Border Panel(View content,Thickness? padding=null)=>new(){Stroke=Stroke,StrokeThickness=1,BackgroundColor=Surface,StrokeShape=new Microsoft.Maui.Controls.Shapes.RoundRectangle{CornerRadius=17},Padding=padding??new Thickness(18),Content=content};
    private static Label T(string text,double size=14,bool bold=false,Color? color=null)=>new(){Text=text,FontSize=size,FontAttributes=bold?FontAttributes.Bold:FontAttributes.None,TextColor=color??Ink,VerticalTextAlignment=TextAlignment.Center};
    private static Entry E(string placeholder,string? value=null)=>new(){Placeholder=placeholder,Text=value,TextColor=Ink,PlaceholderColor=Muted,BackgroundColor=Raised,HeightRequest=46,ClearButtonVisibility=ClearButtonVisibility.WhileEditing};
    private static Button B(string text,Action action,bool primary=false){var b=new Button{Text=text,TextColor=primary?Bg:Ink,BackgroundColor=primary?Cyan:Raised,CornerRadius=9,Padding=new Thickness(15,9),FontAttributes=FontAttributes.Bold,FontSize=13};b.Clicked+=(_,_)=>action();return b;}
    private static Label Chip(string text,Color color)=>new(){Text=text,TextColor=color,BackgroundColor=Raised,Padding=new Thickness(9,5),Margin=new Thickness(3),FontSize=12};
    private static Label Badge(string text,Color color)=>new(){Text=text,TextColor=color,BackgroundColor=Raised,Padding=new Thickness(10,7),FontSize=11,FontAttributes=FontAttributes.Bold,VerticalTextAlignment=TextAlignment.Center};
    private static void Set(Label? label,string text,Color color){if(label is null)return;label.Text=text;label.TextColor=color;}
    private static string? ReadString(JsonElement root,string key)=>root.ValueKind==JsonValueKind.Object&&root.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString():null;
    private static string Str(JsonElement root,string key)=>ReadString(root,key)??"";
    private static long Num(JsonElement root,string key)=>root.TryGetProperty(key,out var v)&&v.TryGetInt64(out var n)?n:0;
    private static string ShortAddress(string? address)=>string.IsNullOrEmpty(address)?"skyf1…":address.Length>24?address[..12]+"…"+address[^8..]:address;
    private static string Format(long seconds){var t=TimeSpan.FromSeconds(Math.Max(0,seconds));return t.TotalDays>=1?$"{(int)t.TotalDays}d {t.Hours:00}h {t.Minutes:00}m":t.TotalHours>=1?$"{(int)t.TotalHours}h {t.Minutes:00}m":$"{t.Minutes}m {t.Seconds:00}s";}
    private static string GetConfiguredDataDirectory() => NodeProcessLauncher.IsInstalled ? NodeProcessLauncher.InstalledDataDirectory : Preferences.Get("skyframe-data-dir", GetDefaultDataDirectory());
    private static string GetDefaultDataDirectory(){foreach(var start in new[]{Environment.CurrentDirectory,AppContext.BaseDirectory})for(var directory=new DirectoryInfo(start);directory is not null;directory=directory.Parent)if(File.Exists(Path.Combine(directory.FullName,"SkyframeNetwork.sln")))return Path.Combine(directory.FullName,"Skyframe","data");return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"Skyframe","data");}
}
