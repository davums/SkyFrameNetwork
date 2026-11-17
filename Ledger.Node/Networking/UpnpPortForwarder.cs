using System.Net;
using System.Runtime.InteropServices;
using System.Net.Sockets;

namespace Skyframe.Ledger.Node.Networking;

internal static class UpnpPortForwarder
{
    public static bool TryOpenTcp(int port, out string message)
    {
        message = "UPnP is unavailable.";
        if (!OperatingSystem.IsWindows()) return false;
        object? device = null;
        object? mappings = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
            if (type is null) { message = "Windows NATUPnP is not installed."; return false; }
            device = Activator.CreateInstance(type);
            if (device is null) { message = "Could not initialize Windows NATUPnP."; return false; }
            dynamic nat = device;
            mappings = nat.StaticPortMappingCollection;
            if (mappings is null) { message = "The router does not expose a UPnP port mapping service."; return false; }
            dynamic collection = mappings;
            var address = GetLanAddress();
            try { collection.Remove(port, "TCP"); } catch { }
            collection.Add(port, "TCP", port, address, true, "Skyframe Node P2P");
            message = $"UPnP forwarded TCP {port} to {address}.";
            return true;
        }
        catch (Exception ex) { message = $"UPnP port forwarding was not enabled: {ex.GetBaseException().Message}"; return false; }
        finally
        {
            if (mappings is not null && Marshal.IsComObject(mappings)) Marshal.FinalReleaseComObject(mappings);
            if (device is not null && Marshal.IsComObject(device)) Marshal.FinalReleaseComObject(device);
        }
    }

    public static void CloseTcp(int port)
    {
        if (!OperatingSystem.IsWindows()) return;
        object? device = null;
        object? mappings = null;
        try
        {
            var type = Type.GetTypeFromProgID("HNetCfg.NATUPnP");
            if (type is null) return;
            device = Activator.CreateInstance(type);
            if (device is null) return;
            dynamic nat = device;
            mappings = nat.StaticPortMappingCollection;
            if (mappings is null) return;
            dynamic collection = mappings;
            collection.Remove(port, "TCP");
        }
        catch { }
        finally
        {
            if (mappings is not null && Marshal.IsComObject(mappings)) Marshal.FinalReleaseComObject(mappings);
            if (device is not null && Marshal.IsComObject(device)) Marshal.FinalReleaseComObject(device);
        }
    }

    private static string GetLanAddress()
    {
        var address = Dns.GetHostAddresses(Dns.GetHostName()).FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork
            && !IPAddress.IsLoopback(ip) && !ip.ToString().StartsWith("169.254.", StringComparison.Ordinal));
        return address?.ToString() ?? throw new InvalidOperationException("No active IPv4 LAN address was found.");
    }
}