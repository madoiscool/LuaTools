using System.IO;
using Microsoft.Win32;

namespace GameGuy.Services;

public static class ProtocolService
{
    private static string ProtocolName => AppConfig.ProtocolScheme;

    private static readonly string PendingFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GameGuy", "protocol_url.tmp");

    public static void Register()
    {
        try
        {
            string exePath = Environment.ProcessPath ?? "";
            using var key = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{ProtocolName}\shell\open\command");
            key.SetValue("", $"\"{exePath}\" \"%1\"");

            using var protoKey = Registry.CurrentUser.CreateSubKey(
                $@"Software\Classes\{ProtocolName}");
            protoKey.SetValue("", "URL:GameGuy Protocol");
            protoKey.SetValue("URL Protocol", "");
        }
        catch { }
    }

    public static (string? Action, long? AppId, bool Silent) Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, null, false);
        Uri uri;
        try { uri = new Uri(url); }
        catch { return (null, null, false); }

        if (!uri.Scheme.Equals(ProtocolName, StringComparison.OrdinalIgnoreCase))
            return (null, null, false);

        string action = uri.Authority.ToLowerInvariant();
        if (action is not ("game" or "install" or "manage" or "fix"))
            return (null, null, false);

        string id = uri.AbsolutePath.TrimStart('/');

        // gameguy://install/silent/<appid> → run the install headless (tray only + a balloon when done).
        bool silent = false;
        if (action == "install" && id.StartsWith("silent/", StringComparison.OrdinalIgnoreCase))
        {
            silent = true;
            id = id["silent/".Length..];
        }

        if (!long.TryParse(id, out long appId)) return (null, null, false);

        return (action, appId, silent);
    }

    public static void WritePending(string url)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PendingFile)!);
            File.WriteAllText(PendingFile, url);
        }
        catch { }
    }

    public static string? TryReadPending()
    {
        try
        {
            if (File.Exists(PendingFile))
            {
                string url = File.ReadAllText(PendingFile).Trim();
                File.Delete(PendingFile);
                return string.IsNullOrEmpty(url) ? null : url;
            }
        }
        catch { }
        return null;
    }
}
