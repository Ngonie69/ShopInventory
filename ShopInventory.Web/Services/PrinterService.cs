using System.Drawing.Printing;
using System.Management;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ShopInventory.Web.Services;

public interface IPrinterService
{
    List<string> GetInstalledPrinters();
}

[SupportedOSPlatform("windows")]
public class PrinterService : IPrinterService
{
    public List<string> GetInstalledPrinters()
    {
        var printers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) WMI Win32_Printer — lists local + network printers visible to this process identity
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Printer");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    printers.Add(name);
            }
        }
        catch { }

        // 2) System.Drawing fallback
        try
        {
            foreach (string printer in PrinterSettings.InstalledPrinters)
                printers.Add(printer);
        }
        catch { }

        // 3) HKLM Print\Printers — machine-wide list, always readable by service accounts
        try
        {
            using var printKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Print\Printers");
            if (printKey != null)
            {
                foreach (var name in printKey.GetSubKeyNames())
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        printers.Add(name);
                }
            }
        }
        catch { }

        // 4) Registry: per-user printer connections and devices for every loaded user profile.
        //    Network printers are installed per-user and are invisible to IIS app pool accounts.
        try
        {
            using var usersKey = Registry.Users;
            foreach (var sid in usersKey.GetSubKeyNames())
            {
                // Skip built-in SIDs like .DEFAULT and _Classes suffixes
                if (!sid.StartsWith("S-1-5-21")) continue;
                if (sid.EndsWith("_Classes")) continue;

                try
                {
                    using var connKey = usersKey.OpenSubKey($@"{sid}\Printers\Connections");
                    if (connKey == null) continue;
                    foreach (var sub in connKey.GetSubKeyNames())
                    {
                        // Connection subkey names use commas: ,,server,printerName
                        var friendly = sub.Replace(',', '\\').TrimStart('\\');
                        if (!string.IsNullOrWhiteSpace(friendly))
                            printers.Add(friendly);
                    }
                }
                catch { }

                // Also check per-user DeviceOld / Devices for locally-added printers
                try
                {
                    using var devKey = usersKey.OpenSubKey($@"{sid}\Software\Microsoft\Windows NT\CurrentVersion\Devices");
                    if (devKey == null) continue;
                    foreach (var valueName in devKey.GetValueNames())
                    {
                        if (!string.IsNullOrWhiteSpace(valueName))
                            printers.Add(valueName);
                    }
                }
                catch { }
            }
        }
        catch { }

        var sorted = printers.ToList();
        sorted.Sort(StringComparer.OrdinalIgnoreCase);
        return sorted;
    }
}
