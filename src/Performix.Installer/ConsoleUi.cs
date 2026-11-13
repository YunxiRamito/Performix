using System.Text;

namespace Performix.Installer;

/// <summary>
/// The whole user interface: console text, and a progress bar drawn with <c>\r</c>.
///
/// Deliberately not a GUI framework (see the csproj comment): the installer has to fit in a
/// ~15 MB trimmed single file, and the progress bar is the only thing a GUI would buy here.
/// When output is redirected (CI, file log) the animated line is replaced by one line per 10%
/// so logs stay readable instead of holding thousands of carriage returns.
/// </summary>
public sealed class ConsoleUi
{
    private const int BarWidth = 24;
    private readonly bool _silent;
    private readonly bool _interactive;
    private int _lastQuantizedPercent = -1;
    private bool _progressLineOpen;

    public ConsoleUi(bool silent)
    {
        _silent = silent;
        _interactive = !Console.IsOutputRedirected;
    }

    public bool Silent => _silent;

    public void Banner(string product, string version)
    {
        if (_silent)
        {
            return;
        }

        WriteLine(ConsoleColor.Cyan, $"Performix Setup {version}");
        WriteLine(ConsoleColor.DarkGray, $"{product} · Windows x64 · 安装器");
        WriteLine(ConsoleColor.DarkGray, new string('─', 56));
    }

    /// <summary>Section header: what we are about to do.</summary>
    public void Step(string title)
    {
        if (_silent)
        {
            return;
        }

        CloseProgressLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine();
        Console.WriteLine($"» {title}");
        Console.ResetColor();
    }

    public void Info(string message)
    {
        if (_silent)
        {
            return;
        }

        CloseProgressLine();
        Write(ConsoleColor.Gray, "  ");
        WriteLine(ConsoleColor.Gray, message);
    }

    public void Detail(string message)
    {
        if (_silent)
        {
            return;
        }

        CloseProgressLine();
        WriteLine(ConsoleColor.DarkGray, "    " + message);
    }

    public void Ok(string message)
    {
        if (_silent)
        {
            return;
        }

        CloseProgressLine();
        Write(ConsoleColor.Green, "  ✓ ");
        WriteLine(ConsoleColor.Gray, message);
    }

    public void Warn(string message)
    {
        CloseProgressLine();
        Write(ConsoleColor.Yellow, "  ! ");
        WriteLine(ConsoleColor.Yellow, message);
    }

    public void Error(string message)
    {
        CloseProgressLine();
        Write(ConsoleColor.Red, "  ✗ ");
        WriteLine(ConsoleColor.Red, message);
    }

    /// <summary>
    /// Renders one progress frame. Called from the download workers (any thread), so the console
    /// state is serialised behind a lock.
    /// </summary>
    public void Progress(DownloadProgress progress, string label = "下载")
    {
        if (_silent)
        {
            return;
        }

        int percent = (int)Math.Floor(progress.Percent);
        string line = ComposeProgressLine(progress, percent, label);

        if (!_interactive)
        {
            int bucket = percent / 10;
            if (bucket == _lastQuantizedPercent)
            {
                return;
            }

            _lastQuantizedPercent = bucket;
            Console.WriteLine(line);
            return;
        }

        lock (_interactiveLock)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write('\r');
            Console.Write(line);
            Console.ResetColor();
            _progressLineOpen = true;
        }
    }

    private static readonly object _interactiveLock = new();

    /// <summary>Ends the animated line so following messages start on a fresh line.</summary>
    public void CloseProgressLine()
    {
        if (!_progressLineOpen)
        {
            return;
        }

        lock (_interactiveLock)
        {
            if (!_progressLineOpen)
            {
                return;
            }

            Console.WriteLine();
            _progressLineOpen = false;
        }
    }

    private static string ComposeProgressLine(DownloadProgress progress, int percent, string label)
    {
        int filled = (int)Math.Round(BarWidth * (percent / 100.0));
        var bar = new StringBuilder(BarWidth);
        for (int i = 0; i < BarWidth; i++)
        {
            bar.Append(i < filled ? '#' : '-');
        }

        string speed = progress.BytesPerSecond > 1 ? FormatSpeed(progress.BytesPerSecond) + "/s" : "…";
        string remaining = progress.Remaining is TimeSpan eta ? FormatRemaining(eta) : "--:--";
        string total = progress.Total > 0 ? FormatBytes(progress.Total) : "?";
        string received = FormatBytes(progress.Received);

        return $"  {label} {percent,3}% [{bar}] {received} / {total}  {speed}  剩 {remaining}  ";
    }

    /// <summary>1024-based sizes with the unit carried into the next one when rounding overflows.</summary>
    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            bytes = 0;
        }

        if (bytes < 1024)
        {
            return $"{bytes} B";
        }

        string[] units = { "KB", "MB", "GB", "TB" };
        double value = bytes;
        int unit = -1;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        // 1023.999 KB must print as "1.0 MB", never as "1024.0 KB".
        string formatted = value >= 99.95 ? $"{value:0}" : $"{value:0.0}";
        return $"{formatted} {units[unit]}";
    }

    public static string FormatSpeed(double bytesPerSecond)
    {
        if (bytesPerSecond < 1)
        {
            return "0 B";
        }

        return FormatBytes((long)Math.Round(bytesPerSecond));
    }

    public static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining < TimeSpan.Zero)
        {
            return string.Empty;
        }

        return remaining.TotalHours >= 1
            ? $"{(int)remaining.TotalHours}:{remaining.Minutes:00}:{remaining.Seconds:00}"
            : $"{remaining.Minutes:00}:{remaining.Seconds:00}";
    }

    /// <summary>Writes the usage text. Goes to stdout even in --silent mode: --help is an answer.</summary>
    public static void PrintHelp(string version)
    {
        Console.WriteLine($"""
Performix Setup {version} — 安装 Performix（Windows x64）

用法：
  PerformixSetup.exe [选项]

模式：
  （无）                安装：检查 .NET 10 桌面运行时 → 缺了就下载安装 → 装 Performix → 建快捷方式 → 启动
  --uninstall          卸载：删开始菜单/桌面快捷方式、删 app 目录，保留 preysense.json 并提示位置
  --help               显示这段说明并退出

选项：
  --version <tag>      安装指定版本（GitHub release tag，例如 v0.1.0-alpha.2；开头的 v 可省略）；默认装最新版
                       （最新版包含预发布版本：正式版 > beta > alpha）
  --offline <zip>      用本地 zip 安装，不联网（CI 自测 / 内网部署）
  --dir <path>         覆盖安装根目录（默认 %LOCALAPPDATA%\Performix\app；版本仍装成子目录）
  --no-launch          装完不启动 Performix
  --desktop-shortcut   额外创建桌面快捷方式（默认只建开始菜单的）
  --silent             静默：只输出错误
  任何开关也可以写成 --name=true/false/1/0。

示例：
  PerformixSetup.exe
  PerformixSetup.exe --version v0.1.0-alpha.2 --desktop-shortcut
  PerformixSetup.exe --offline .\Performix-win-x64.zip --no-launch --silent
  PerformixSetup.exe --uninstall

安装位置：
  程序        %LOCALAPPDATA%\Performix\app\<version>\
  快捷方式    %APPDATA%\Microsoft\Windows\Start Menu\Programs\Performix.lnk
  配置        preysense.json（在程序目录里；卸载时搬到 %LOCALAPPDATA%\Performix\）

退出码：
  0  成功           1  未预期错误      2  命令行错误
  3  运行时下载/校验失败                 4  运行时安装失败
  5  获取 release 失败                   6  程序包下载/校验失败
  7  解压或安装失败
""");
    }

    private static void Write(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ResetColor();
    }

    private static void WriteLine(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
