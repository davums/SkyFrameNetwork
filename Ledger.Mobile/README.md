# Ledger.Mobile

Ledger.Mobile targets .NET 9 MAUI on Android and Windows desktop (unpackaged); iOS is currently disabled. The Windows app is sized like a phone window (410 × 820). The recovery phrase is stored with SecureStorage.Default and is never included in an HTTP request. Transactions are signed in-process and only the signed transaction is broadcast.

## Android development

Install Android SDK Platform 35 and Android SDK Command-line Tools (latest) using Android Studio's SDK Manager. Set `ANDROID_HOME` to the SDK directory or pass `-p:AndroidSdkDirectory` when building. In PowerShell, build with `dotnet build Ledger.Mobile/Ledger.Mobile.csproj -f net9.0-android -p:AndroidSdkDirectory="$env:ANDROID_HOME"`.

## Node gateways

No public seed host list is configured in this repository. Add one or more independent HTTPS node gateway URLs in Settings. Hostnames are resolved by DNS, and the entered list is cached on-device. For each operation, the app randomly selects up to five, checks /status, requires the official genesis hash, and requires matching responses from `Math.Clamp(configuredGatewayCount, 1, 5)` nodes. The local Windows endpoint is the one-node exception for desktop testing.

Node operators can enable the optional public REST API with Ledger.Node --public-api. It listens on P2P port + 1001 (port 6002 with the default P2P port), rate-limits clients, and caps request bodies. Put that HTTP listener behind an HTTPS reverse proxy and expose these routes: /status, /balance/{address}, /escrows, /tx, /mempool/{txId}, and /payments/lookup. Keep the local control API (P2P port + 1000) loopback-only; it contains node start/stop controls.

## QR features

Payment QR codes use `skyframe:{address}?amount={amount}&reference={SKYF-XXXXXX}`. For ZAR merchant pricing, enter the SKYF-per-ZAR quote that your business has chosen; the app does not assume or fetch an exchange rate. The POS checks the request against the configured dynamic quorum and reports a payment only after matching mempool or block observations.

Skyframe uses its own `SKYF` network magic, `skyf1` address prefix, and genesis identity. Escrow EFT references use `SKYF-XXXXXX`. Existing Kora chain data is preserved in the old `data` directory; the new node defaults to `Skyframe/data` (or `%LOCALAPPDATA%/Skyframe/data` when run outside the solution). The Android application ID is `com.skyframe.ledger.mobile`; Android treats it as a new app identity, so import the existing recovery phrase to restore the wallet.

For local Windows development, enter `http://127.0.0.1:6001` in Settings to use the node's loopback API in single-node mode. This mode is for local testing. For public operation, configure independent HTTPS gateways; the app scales the required matching responses from one to five as gateways are added.

