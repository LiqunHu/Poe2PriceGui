using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Poe2PriceGui.Models;

namespace Poe2PriceGui.Services;

/// <summary>
/// 通过已安装软件列表、游戏平台配置等自动查找 POE2 游戏目录。
/// </summary>
public static class GameDirectoryDetector
{
    /// <summary>
    /// 查找所有候选目录。
    /// </summary>
    public static List<GameDirectoryCandidate> FindCandidates()
    {
        var candidates = new List<GameDirectoryCandidate>();

        AddGggRegistryCandidates(candidates);
        AddRegistryCandidates(candidates);
        AddSteamCandidates(candidates);
        AddEpicCandidates(candidates);
        AddWeGameDefaultCandidates(candidates);
        AddShortcutCandidates(candidates);
        //AddRunningProcessCandidates(candidates);
        AddKnownPathCandidates(candidates);
        AddDriveRootCandidates(candidates);

        // 去重并按路径排序。
        return candidates
            .GroupBy(c => c.Path)
            .Select(g => g.First())
            .OrderBy(c => c.Region)
            .ThenBy(c => c.Path)
            .ToList();
    }

    /// <summary>
    /// GGG 官方安装器注册表：HKCU\SOFTWARE\GrindingGearGames\Path of Exile 2
    /// 这是国际服官方安装器写入的注册表，直接包含完整安装路径。
    /// </summary>
    private static void AddGggRegistryCandidates(List<GameDirectoryCandidate> candidates)
    {
        var gggRoots = new[]
        {
            Registry.CurrentUser.OpenSubKey(@"SOFTWARE\GrindingGearGames"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\GrindingGearGames"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\GrindingGearGames"),
        };

        foreach (var root in gggRoots)
        {
            if (root == null) continue;
            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var installLocation = subKey.GetValue("InstallLocation") as string ?? "";
                if (!string.IsNullOrEmpty(installLocation))
                {
                    TryAddCandidate(candidates, installLocation, $"GGG 注册表：{subKeyName}");
                }
            }
        }
    }

    private static void AddRegistryCandidates(List<GameDirectoryCandidate> candidates)
    {
        var searchNames = new[] { "Path of Exile 2", "流放之路：降临", "流放之路", "pathofexile", "poe2" };
        var uninstallRoots = new[]
        {
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };

        foreach (var root in uninstallRoots)
        {
            if (root == null) continue;
            foreach (var subKeyName in root.GetSubKeyNames())
            {
                using var subKey = root.OpenSubKey(subKeyName);
                if (subKey == null) continue;

                var displayName = subKey.GetValue("DisplayName") as string ?? "";

                // 同时检查 DisplayName 和注册表键名中的关键词
                var matchesName = searchNames.Any(n => displayName.Contains(n, StringComparison.OrdinalIgnoreCase));
                var matchesKey = searchNames.Any(n => subKeyName.Contains(n, StringComparison.OrdinalIgnoreCase));

                if (!matchesName && !matchesKey) continue;

                // InstallLocation 可能是父目录，也尝试子目录
                var installLocation = subKey.GetValue("InstallLocation") as string ?? "";
                if (!string.IsNullOrEmpty(installLocation))
                {
                    TryAddCandidate(candidates, installLocation, $"已安装软件：{displayName}");
                    if (Directory.Exists(installLocation))
                    {
                        foreach (var dir in Directory.GetDirectories(installLocation))
                        {
                            TryAddCandidate(candidates, dir, $"已安装软件子目录：{Path.GetFileName(dir)}");
                        }
                    }
                }

                // InstallLocation 为空时，从其他字段提取路径
                // UninstallString、InstallSource、DisplayIcon 可能包含完整路径
                var pathFields = new[] { "UninstallString", "InstallSource", "DisplayIcon", "ModifyPath" };
                foreach (var field in pathFields)
                {
                    var value = subKey.GetValue(field) as string ?? "";
                    if (string.IsNullOrEmpty(value)) continue;

                    var extractedPath = ExtractPathFromFieldValue(value);
                    if (extractedPath == null) continue;

                    // 向上查找游戏根目录（包含 Content.ggpk 或 Bundles2/_.index.bin）
                    var gameRoot = FindGameRootFromPath(extractedPath);
                    if (gameRoot != null)
                    {
                        TryAddCandidate(candidates, gameRoot, $"注册表{field}提取：{displayName}");
                    }
                }
            }
        }
    }

    /// <summary>
    /// 从注册表字段值中提取路径。
    /// 处理引号、参数、图标索引（如 ",0"）。
    /// </summary>
    private static string? ExtractPathFromFieldValue(string value)
    {
        value = value.Trim('"', '\'');
        // 去掉参数（如 "C:\path\app.exe -arg" → "C:\path\app.exe"）
        if (value.Contains(' '))
        {
            value = value.Split(' ')[0].Trim('"');
        }
        // 去掉 ",0" 之类的图标索引
        var commaIdx = value.LastIndexOf(',');
        if (commaIdx >= 0 && value.Substring(commaIdx + 1).Trim().All(c => char.IsDigit(c)))
            value = value.Substring(0, commaIdx);

        if (File.Exists(value))
            return Path.GetDirectoryName(value);
        if (Directory.Exists(value))
            return value;
        return null;
    }

    /// <summary>
    /// 从给定路径向上查找（最多5层）包含 Content.ggpk 或 Bundles2/_.index.bin 的游戏根目录。
    /// 同时检查同级子目录。
    /// </summary>
    private static string? FindGameRootFromPath(string path)
    {
        var current = path;
        for (int i = 0; i < 5 && current != null; i++)
        {
            if (File.Exists(Path.Combine(current, "Content.ggpk")) ||
                File.Exists(Path.Combine(current, "Bundles2", "_.index.bin")))
                return current;

            try
            {
                foreach (var dir in Directory.GetDirectories(current))
                {
                    if (File.Exists(Path.Combine(dir, "Content.ggpk")) ||
                        File.Exists(Path.Combine(dir, "Bundles2", "_.index.bin")))
                        return dir;
                }
            }
            catch { }

            current = Path.GetDirectoryName(current);
        }
        return null;
    }

    private static void AddSteamCandidates(List<GameDirectoryCandidate> candidates)
    {
        var libraryFolders = FindSteamLibraryFolders();
        foreach (var libraryPath in libraryFolders)
        {
            var gamePath = Path.Combine(libraryPath, "steamapps", "common", "Path of Exile 2");
            TryAddCandidate(candidates, gamePath, "Steam 库");

            // 也检查小写/非标准目录名
            var commonDir = Path.Combine(libraryPath, "steamapps", "common");
            if (Directory.Exists(commonDir))
            {
                foreach (var dir in Directory.GetDirectories(commonDir))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Contains("pathofexile", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("poe", StringComparison.OrdinalIgnoreCase))
                    {
                        TryAddCandidate(candidates, dir, "Steam 库（非标准目录名）");
                    }
                }
            }
        }
    }

    private static List<string> FindSteamLibraryFolders()
    {
        var paths = new List<string>();

        // 优先从注册表找 Steam 路径（不依赖默认安装位置）
        var steamRegCu = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Valve\Steam");
        var steamPathCu = steamRegCu?.GetValue("SteamPath") as string ?? "";
        var steamRegLm = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam");
        var steamPathLm = steamRegLm?.GetValue("InstallPath") as string ?? "";

        var steamPath = !string.IsNullOrEmpty(steamPathCu) ? steamPathCu.Replace('/', '\\') : steamPathLm;
        if (string.IsNullOrEmpty(steamPath))
        {
            // 回退到默认路径
            steamPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam");
        }

        var defaultVdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(defaultVdf))
        {
            ExtractVdfPaths(defaultVdf, paths);
            paths.Add(Path.Combine(steamPath, "steamapps"));
        }

        return paths.Distinct().ToList();
    }

    private static void ExtractVdfPaths(string vdfPath, List<string> paths)
    {
        try
        {
            var text = File.ReadAllText(vdfPath);
            // VDF 格式示例："path"\t\t"D:\\SteamLibrary"
            var matches = Regex.Matches(text, "\"path\"\\s+\"([^\"]+)\"");
            foreach (Match match in matches)
            {
                var rawPath = match.Groups[1].Value.Replace("\\\\", "\\").Trim();
                if (Directory.Exists(rawPath))
                {
                    paths.Add(Path.Combine(rawPath, "steamapps"));
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warn($"解析 Steam libraryfolders.vdf 失败：{ex.Message}");
        }
    }

    private static void AddEpicCandidates(List<GameDirectoryCandidate> candidates)
    {
        var manifestDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifestDir)) return;

        foreach (var itemFile in Directory.GetFiles(manifestDir, "*.item"))
        {
            try
            {
                var json = File.ReadAllText(itemFile);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("DisplayName", out var displayNameElement) ||
                    !root.TryGetProperty("InstallLocation", out var installLocationElement))
                {
                    continue;
                }

                var displayName = displayNameElement.GetString() ?? "";
                var installLocation = installLocationElement.GetString() ?? "";

                if (!displayName.Contains("Path of Exile 2", StringComparison.OrdinalIgnoreCase))
                    continue;

                TryAddCandidate(candidates, installLocation, "Epic Games");
            }
            catch (Exception ex)
            {
                AppLogger.Instance.Warn($"解析 Epic manifest 失败 {itemFile}：{ex.Message}");
            }
        }
    }

    private static void AddWeGameDefaultCandidates(List<GameDirectoryCandidate> candidates)
    {
        var weGameBases = new[]
        {
            Path.Combine("C:", "WeGameApps", "rail_apps"),
            Path.Combine("D:", "WeGameApps", "rail_apps"),
            Path.Combine("E:", "WeGameApps", "rail_apps"),
            Path.Combine("F:", "WeGameApps", "rail_apps"),
            Path.Combine("G:", "WeGameApps", "rail_apps"),
        };

        foreach (var basePath in weGameBases)
        {
            if (!Directory.Exists(basePath)) continue;
            foreach (var dir in Directory.GetDirectories(basePath))
            {
                var name = Path.GetFileName(dir);
                if (name.Contains("流放之路", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Path of Exile", StringComparison.OrdinalIgnoreCase))
                {
                    TryAddCandidate(candidates, dir, "WeGame 默认路径");
                }
            }
        }
    }

    /// <summary>
    /// 检查已知的常见游戏安装路径（包括官网独立安装器、自定义路径等）。
    /// </summary>
    private static void AddKnownPathCandidates(List<GameDirectoryCandidate> candidates)
    {
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed)
            .Select(d => d.RootDirectory.FullName.TrimEnd('\\'));

        // 常见的目录名模式（覆盖各种命名习惯）
        var dirNames = new[]
        {
            "Path of Exile 2",
            "pathofexile2",
            "pathofexite2",       // 常见拼写
            "poe2",
            "PathOfExile2",
            "流放之路2",
            "流放之路：降临",
        };

        // 可能的父目录
        var parentPatterns = new List<string>();
        foreach (var drive in drives)
        {
            // 根目录下直接放游戏
            foreach (var name in dirNames)
            {
                parentPatterns.Add(Path.Combine(drive, name));
            }
            // Games 子目录
            parentPatterns.Add(Path.Combine(drive, "Games"));
            parentPatterns.Add(Path.Combine(drive, "Game"));
            // Program Files
            parentPatterns.Add(Path.Combine(drive, "Program Files"));
            parentPatterns.Add(Path.Combine(drive, "Program Files (x86)"));
        }

        foreach (var parentPath in parentPatterns)
        {
            if (!Directory.Exists(parentPath)) continue;

            // 父路径本身可能是游戏目录
            TryAddCandidate(candidates, parentPath, "常见路径");

            // 检查子目录
            try
            {
                foreach (var dir in Directory.GetDirectories(parentPath))
                {
                    var name = Path.GetFileName(dir);
                    if (dirNames.Any(n => name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                    {
                        TryAddCandidate(candidates, dir, "常见路径");
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 遍历所有固定磁盘根目录下的一级子目录，匹配 POE2 关键词。
    /// 这是最宽泛的搜索，用于兜底。
    /// </summary>
    private static void AddDriveRootCandidates(List<GameDirectoryCandidate> candidates)
    {
        var keywords = new[] { "pathofexile", "pathofexite", "poe2", "流放之路" };
        var drives = DriveInfo.GetDrives()
            .Where(d => d.IsReady && d.DriveType == DriveType.Fixed);

        foreach (var drive in drives)
        {
            var root = drive.RootDirectory.FullName;
            try
            {
                foreach (var dir in Directory.GetDirectories(root))
                {
                    var name = Path.GetFileName(dir);
                    if (keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    {
                        TryAddCandidate(candidates, dir, "磁盘根目录扫描");
                    }
                }
            }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// 扫描开始菜单、桌面快捷方式，解析 .lnk 目标路径。
    /// 通用方法：只要游戏安装时创建了快捷方式就能找到。
    /// </summary>
    private static void AddShortcutCandidates(List<GameDirectoryCandidate> candidates)
    {
        var shortcutDirs = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Microsoft", "Windows", "Start Menu", "Programs"),
        };

        var keywords = new[] { "exile", "poe", "流放", "path of exile" };

        foreach (var dir in shortcutDirs.Distinct().Where(Directory.Exists))
        {
            try
            {
                foreach (var lnk in Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories))
                {
                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (!keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var target = ResolveShortcutTarget(lnk);
                    if (target == null) continue;

                    var gameRoot = FindGameRootFromPath(target);
                    if (gameRoot != null)
                    {
                        TryAddCandidate(candidates, gameRoot, "快捷方式解析");
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// 解析 .lnk 快捷方式的目标路径（使用 WScript.Shell COM 对象）。
    /// </summary>
    private static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return null;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(lnkPath);
            return (string)shortcut.TargetPath;
        }
        catch { return null; }
    }

    /// <summary>
    /// 从运行中的游戏进程提取安装目录。
    /// 如果游戏正在运行，这是最可靠的方法。
    /// </summary>
    private static void AddRunningProcessCandidates(List<GameDirectoryCandidate> candidates)
    {
        var keywords = new[] { "pathofexile", "poe2", "exile" };
        var processes = System.Diagnostics.Process.GetProcesses();

        foreach (var proc in processes)
        {
            try
            {
                var name = proc.ProcessName;
                if (!keywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var mainModule = proc.MainModule;
                if (mainModule == null) continue;

                var exePath = mainModule.FileName;
                var gameRoot = FindGameRootFromPath(exePath);
                if (gameRoot != null)
                {
                    TryAddCandidate(candidates, gameRoot, "运行进程");
                }
            }
            catch { }
        }
    }

    private static void TryAddCandidate(List<GameDirectoryCandidate> candidates, string path, string source)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        path = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(path)) return;

        var info = GameModeDetector.Detect(path);
        if (!info.IsValid) return;

        var region = info.IsChina ? ServerRegion.China : ServerRegion.International;
        candidates.Add(new GameDirectoryCandidate
        {
            Path = path,
            Region = region,
            Source = source,
        });
    }
}
