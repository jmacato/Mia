// SPDX-License-Identifier: MIT

namespace Mia.Desktop.Hosting;

internal sealed record DesktopOptions(
    string RepositoryRoot,
    string FirmwarePath,
    string GdfsPath,
    string GdfsOverlayPath,
    string ModemPath,
    string FlashUserOtp,
    bool AutoStart,
    bool PaceToRealTime,
    bool IgnoreGdfsOverlay,
    int AutomationPort)
{
    const string DefaultModemFileName = "t68i_R8A015_125326_Modem.bih";

    public static DesktopOptions Parse(IReadOnlyList<string> arguments)
    {
        var root = FindRepositoryRoot(Environment.CurrentDirectory) ??
            FindRepositoryRoot(AppContext.BaseDirectory) ??
            Environment.CurrentDirectory;
        var firmware = System.IO.Path.Combine(root, "flat.bin");
        var originalGdfs = System.IO.Path.Combine(root, "images", "T68i_Full_GDFS.raw");
        var compactGdfs = System.IO.Path.Combine(root, "images", "T68i_Full_GDFS.compact.raw");
        var defaultGdfs = System.IO.Path.Combine(root, "images", "T68i_Default_GDFS.raw");
        var gdfs = File.Exists(defaultGdfs)
            ? defaultGdfs
            : File.Exists(compactGdfs) ? compactGdfs : originalGdfs;
        string? gdfsOverlay = Environment.GetEnvironmentVariable(
            "MIA_GDFS_OVERLAY_FILE");
        var modem = Environment.GetEnvironmentVariable("MIA_MODEM_FILE") ??
            System.IO.Path.Combine(root, "images", DefaultModemFileName);
        var otp = "321A065432100654";
        var autoStart = true;
        var paceToRealTime = true;
        var ignoreGdfsOverlay = false;
        var automationPort = 46968;

        for (var index = 0; index < arguments.Count; index++)
        {
            switch (arguments[index])
            {
                case "--firmware-file" when index + 1 < arguments.Count:
                    firmware = System.IO.Path.GetFullPath(arguments[++index]);
                    break;
                case "--gdfs-file" when index + 1 < arguments.Count:
                    gdfs = System.IO.Path.GetFullPath(arguments[++index]);
                    break;
                case "--gdfs-overlay-file" when index + 1 < arguments.Count:
                    gdfsOverlay = System.IO.Path.GetFullPath(arguments[++index]);
                    break;
                case "--fresh-gdfs":
                    ignoreGdfsOverlay = true;
                    break;
                case "--modem-file" when index + 1 < arguments.Count:
                    modem = System.IO.Path.GetFullPath(arguments[++index]);
                    break;
                case "--flash-user-otp" when index + 1 < arguments.Count:
                    otp = arguments[++index];
                    break;
                case "--no-auto-start":
                    autoStart = false;
                    break;
                case "--unthrottled":
                    paceToRealTime = false;
                    break;
                case "--automation-port" when index + 1 < arguments.Count &&
                    int.TryParse(arguments[++index], out var parsedAutomationPort) &&
                    parsedAutomationPort is >= 0 and <= 65535:
                    automationPort = parsedAutomationPort;
                    break;
            }
        }
        gdfsOverlay = System.IO.Path.GetFullPath(gdfsOverlay ?? gdfs + ".overlay");

        return new DesktopOptions(
            root,
            firmware,
            gdfs,
            gdfsOverlay,
            modem,
            otp,
            autoStart,
            paceToRealTime,
            ignoreGdfsOverlay,
            automationPort);
    }

    public IEnumerable<string> MissingInputs()
    {
        if (!File.Exists(FirmwarePath))
        {
            yield return FirmwarePath;
        }
        if (!File.Exists(GdfsPath))
        {
            yield return GdfsPath;
        }
        if (!File.Exists(ModemPath))
        {
            yield return ModemPath;
        }
    }

    static string? FindRepositoryRoot(string start)
    {
        var directory = new DirectoryInfo(System.IO.Path.GetFullPath(start));
        while (directory is not null)
        {
            if (File.Exists(System.IO.Path.Combine(directory.FullName, "Mia.slnx")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        return null;
    }
}
