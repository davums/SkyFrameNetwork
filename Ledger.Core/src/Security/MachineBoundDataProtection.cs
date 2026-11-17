using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Skyframe.Ledger.Core.Security;

/// <summary>Protects wallet identity and peer metadata at rest using a DPAPI machine-bound key and ChaCha20-Poly1305.</summary>
public static class MachineBoundDataProtection
{
    private const uint CryptProtectLocalMachine = 0x4;
    private const string KeyFileName = "node_machine_key.dpapi";
    private static readonly byte[] KeyEntropy = Encoding.UTF8.GetBytes("SkyframeNetwork.NodeMachineKey.v1");
    private static readonly byte[] IdentityInfo = Encoding.UTF8.GetBytes("SkyframeNetwork.NodeIdentity.v1");
    private static readonly byte[] PeersInfo = Encoding.UTF8.GetBytes("SkyframeNetwork.Peers.v1");
    private static readonly byte[] IdentityAad = Encoding.UTF8.GetBytes("SkyframeNetwork.IdentityEnvelope.v1");
    private static readonly byte[] PeersAad = Encoding.UTF8.GetBytes("SkyframeNetwork.PeersEnvelope.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static void SaveIdentity(string dataDirectory, string identityJson)
    {
        var masterKey = GetMachineKey(dataDirectory, create: true);
        try
        {
            var envelope = Encrypt(identityJson, masterKey, IdentityInfo, IdentityAad, "skyframe-encrypted-identity-v1");
            WriteAtomically(Path.Combine(dataDirectory, "node_identity.json"), JsonSerializer.Serialize(envelope, JsonOptions));
        }
        finally { CryptographicOperations.ZeroMemory(masterKey); }
    }

    public static string LoadIdentity(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, "node_identity.json");
        var masterKey = GetMachineKey(dataDirectory, create: false);
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("Encrypted node identity is empty.");
            return Decrypt(envelope, masterKey, IdentityInfo, IdentityAad, "skyframe-encrypted-identity-v1");
        }
        finally { CryptographicOperations.ZeroMemory(masterKey); }
    }

    public static string EncryptPeersJson(string dataDirectory, string peersJson)
    {
        var masterKey = GetMachineKey(dataDirectory, create: true);
        try { return JsonSerializer.Serialize(Encrypt(peersJson, masterKey, PeersInfo, PeersAad, "skyframe-encrypted-peers-v1"), JsonOptions); }
        finally { CryptographicOperations.ZeroMemory(masterKey); }
    }

    public static string DecryptPeersJson(string dataDirectory, string encryptedPeersJson)
    {
        var masterKey = GetMachineKey(dataDirectory, create: false);
        try
        {
            var envelope = JsonSerializer.Deserialize<Envelope>(encryptedPeersJson, JsonOptions)
                ?? throw new InvalidDataException("Encrypted peer list is empty.");
            return Decrypt(envelope, masterKey, PeersInfo, PeersAad, "skyframe-encrypted-peers-v1");
        }
        finally { CryptographicOperations.ZeroMemory(masterKey); }
    }

    private static Envelope Encrypt(string plaintext, byte[] masterKey, byte[] info, byte[] aad, string format)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = HkdfSha256(masterKey, salt, info, 32);
        var plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plainBytes.Length];
        var tag = new byte[16];
        try
        {
            using var cipher = new ChaCha20Poly1305(key);
            cipher.Encrypt(nonce, plainBytes, ciphertext, tag, aad);
            return new Envelope(format, 1, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(ciphertext), Convert.ToBase64String(tag));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    private static string Decrypt(Envelope envelope, byte[] masterKey, byte[] info, byte[] aad, string expectedFormat)
    {
        if (envelope.Version != 1 || !string.Equals(envelope.Format, expectedFormat, StringComparison.Ordinal))
            throw new InvalidDataException("Unsupported encrypted Skyframe data format.");
        var salt = Convert.FromBase64String(envelope.Salt);
        var nonce = Convert.FromBase64String(envelope.Nonce);
        var ciphertext = Convert.FromBase64String(envelope.Ciphertext);
        var tag = Convert.FromBase64String(envelope.Tag);
        if (salt.Length != 16 || nonce.Length != 12 || tag.Length != 16)
            throw new InvalidDataException("Encrypted Skyframe data has invalid cryptographic field lengths.");
        var key = HkdfSha256(masterKey, salt, info, 32);
        var plaintext = new byte[ciphertext.Length];
        try
        {
            using var cipher = new ChaCha20Poly1305(key);
            cipher.Decrypt(nonce, ciphertext, tag, plaintext, aad);
            return new UTF8Encoding(false, true).GetString(plaintext);
        }
        catch (CryptographicException ex) { throw new InvalidDataException("Skyframe data authentication failed; the file may be damaged or copied from another machine.", ex); }
        finally { CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(plaintext); }
    }

    private static byte[] GetMachineKey(string dataDirectory, bool create)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Node identity protection requires Windows DPAPI machine protection.");
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, KeyFileName);
        if (File.Exists(path))
        {
            var key = UnprotectMachine(File.ReadAllBytes(path));
            if (key.Length != 32) { CryptographicOperations.ZeroMemory(key); throw new InvalidDataException("DPAPI machine key must contain 32 bytes."); }
            return key;
        }
        if (!create) throw new FileNotFoundException("DPAPI machine key is missing; encrypted Skyframe data cannot be opened on this computer.", path);

        var generated = RandomNumberGenerator.GetBytes(32);
        var protectedKey = ProtectMachine(generated);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temp, protectedKey);
            try { File.Move(temp, path, overwrite: false); }
            catch (IOException) when (File.Exists(path)) { }
            if (!File.Exists(path)) throw new IOException("Could not install the DPAPI machine key.");
            var installed = UnprotectMachine(File.ReadAllBytes(path));
            CryptographicOperations.ZeroMemory(generated);
            return installed;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedKey);
            CryptographicOperations.ZeroMemory(generated);
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static byte[] ProtectMachine(byte[] input)
    {
        var inputBlob = Allocate(input);
        var entropyBlob = Allocate(KeyEntropy);
        try
        {
            if (!CryptProtectData(ref inputBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CryptProtectLocalMachine, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not protect the Skyframe machine key.");
            return CopyAndFree(output);
        }
        finally { Free(inputBlob); Free(entropyBlob); }
    }

    private static byte[] UnprotectMachine(byte[] input)
    {
        var inputBlob = Allocate(input);
        var entropyBlob = Allocate(KeyEntropy);
        try
        {
            if (!CryptUnprotectData(ref inputBlob, out var description, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, 0, out var output))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows DPAPI could not unlock the Skyframe machine key. The data may belong to another computer.");
            if (description != IntPtr.Zero) LocalFree(description);
            return CopyAndFree(output);
        }
        finally { Free(inputBlob); Free(entropyBlob); }
    }

    private static byte[] HkdfSha256(byte[] inputKey, byte[] salt, byte[] info, int length)
    {
        var actualSalt = salt.Length == 0 ? new byte[32] : salt;
        var pseudorandomKey = HMACSHA256.HashData(actualSalt, inputKey);
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;
        try
        {
            while (offset < length)
            {
                var blockInput = new byte[previous.Length + info.Length + 1];
                previous.CopyTo(blockInput, 0);
                info.CopyTo(blockInput, previous.Length);
                blockInput[^1] = counter++;
                var block = HMACSHA256.HashData(pseudorandomKey, blockInput);
                CryptographicOperations.ZeroMemory(blockInput);
                if (previous.Length > 0) CryptographicOperations.ZeroMemory(previous);
                previous = block;
                var take = Math.Min(block.Length, length - offset);
                block.AsSpan(0, take).CopyTo(output.AsSpan(offset));
                offset += take;
            }
            return output;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pseudorandomKey);
            if (previous.Length > 0) CryptographicOperations.ZeroMemory(previous);
            if (salt.Length == 0) CryptographicOperations.ZeroMemory(actualSalt);
        }
    }

    private static void WriteAtomically(string path, string contents)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private static DataBlob Allocate(byte[] value)
    {
        var data = Marshal.AllocHGlobal(value.Length);
        Marshal.Copy(value, 0, data, value.Length);
        return new DataBlob { Size = value.Length, Data = data };
    }

    private static byte[] CopyAndFree(DataBlob blob)
    {
        try { var value = new byte[blob.Size]; Marshal.Copy(blob.Data, value, 0, blob.Size); return value; }
        finally { if (blob.Data != IntPtr.Zero) LocalFree(blob.Data); }
    }

    private static void Free(DataBlob blob) { if (blob.Data != IntPtr.Zero) Marshal.FreeHGlobal(blob.Data); }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Size; public IntPtr Data; }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, out IntPtr description, ref DataBlob optionalEntropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr memory);

    private sealed record Envelope(string Format, int Version, string Salt, string Nonce, string Ciphertext, string Tag);
}