using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Picollo;

internal static class PicolloConstants
{
    /// <summary>
    /// Cross-platform Picollo home directory.
    /// Windows: %LocalAppData%\Picollo
    /// Linux:   ~/.local/share/Picollo
    /// macOS:   ~/Library/Application Support/Picollo
    /// </summary>
    public static string PicolloHome { get; } = InitPicolloHome();

    private static string TmpSockets { get; } = InitTmpSockets();

    private static string InitPicolloHome()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
            var sudoUid = Environment.GetEnvironmentVariable("SUDO_UID");
            var home = !string.IsNullOrWhiteSpace(sudoUser) && !string.IsNullOrWhiteSpace(sudoUid)
                ? Path.Combine("/home", sudoUser)
                : Environment.GetEnvironmentVariable("HOME") ?? "/root";

            return Path.Combine(home, ".local", "share", "Picollo");
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            var sudoUser = Environment.GetEnvironmentVariable("SUDO_USER");
            var sudoUid = Environment.GetEnvironmentVariable("SUDO_UID");
            var home = !string.IsNullOrWhiteSpace(sudoUser) && !string.IsNullOrWhiteSpace(sudoUid)
                ? Path.Combine("/Users", sudoUser)
                : Environment.GetEnvironmentVariable("HOME") ?? "/var/root";

            return Path.Combine(home, "Library", "Application Support", "Picollo");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Picollo");
    }

    private static string InitTmpSockets()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Picollo", "sockets"));

        try
        {
            Directory.CreateDirectory(path);
        }
        catch
        {
        }

        return path;
    }

    [Obsolete]
    public static string GetSocketPath(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Bad sessionId");
        return Path.Combine(TmpSockets, $"{sessionId}.sock");
    }

    public static string GetSessionSocketPath(int processId)
    {
        if (processId <= 0)
            throw new ArgumentException("Bad processId");
        
        return Path.Combine(TmpSockets, $"client-session-{processId}.sock");
    }

    /// <summary>Subfolder name within <see cref="PicolloHome"/> used to cache downloaded symbols.</summary>
    public const string SymbolsCache = "SymbolsCache";


    public const string VdsoModuleName = "[vdso]";
    public const string DynamicModuleName = "[dynamic]";
    
    // Is should be a GZiped NDJSON, so it's "profile compressed lines"
    public const string ProfileOutputExtension = ".pcl";
}