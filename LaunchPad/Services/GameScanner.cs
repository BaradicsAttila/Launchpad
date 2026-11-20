

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace LaunchPad.Services;

public class GameScanResult
{
    public string ExePath { get; set; } = string.Empty;
    public string GameName { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string? InstallRoot { get; set; }
}

public class GameScanner
{
    #region Source Priority

    // Lower number = more trustworthy name
    // Steam and Epic store exact official names in their manifest files
    // Start Menu and Registry store display names — usually good
    // Folder-based scanners (Ubisoft, EA, GOG, BattleNet) only know the folder name
    // Deep scan is the least reliable — it only knows the exe filename
    private static readonly Dictionary<string, int> SourcePriority = new()
    {
        { "Steam",     1 },
        { "Epic",      1 },
        { "StartMenu", 2 },
        { "Registry",  3 },
        { "Ubisoft",   4 },
        { "EA",        4 },
        { "GOG",       4 },
        { "BattleNet", 4 },
        { "Scan",      5 },
    };

    #endregion

    #region Excluded Folders (for deep scan)

    private static readonly HashSet<string> ExcludedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        @"C:\Windows",
        @"C:\Windows\WinSxS",
        @"C:\Windows\System32",
        @"C:\Windows\SysWOW64",
        @"C:\Windows\servicing",
        @"C:\System Volume Information",
        @"C:\Recovery",
        @"C:\$Recycle.Bin",
        @"C:\ProgramData\Microsoft",
        @"C:\ProgramData\Package Cache",
    };

    private static readonly HashSet<string> ExcludedAppDataFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Microsoft", "Temp", "Google", "Mozilla", "Packages"
    };

    #endregion

    #region Excluded Path Roots (instant scannerekhez)

    private static readonly string WindowsFolder =
        Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static readonly string AppDataRoaming =
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    private static readonly string AppDataLocal =
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    private static readonly string ProgramFiles =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);

    private static readonly string ProgramFilesX86 =
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

    // Program Files alatt ELVILEG minden telepitett program ott van (nem csak
    // jatek), ezert csak az ismert jatek-platform almappakat engedjuk at -
    // ezek ugyanazok, ahova a GOG/Ubisoft/EA/Steam sajat scannerei is neznek.
    private static readonly string[] KnownGamePlatformMarkers = new[]
    {
        @"\steamapps\common\",
        @"\gog galaxy\games\",
        @"\ubisoft game launcher\games\",
        @"\ea games\",
        @"\origin games\",
    };

    private static bool IsKnownGamePlatformPath(string path)
    {
        var lower = path.ToLowerInvariant();
        return KnownGamePlatformMarkers.Any(marker => lower.Contains(marker));
    }

    private static bool IsUnderExcludedRoot(string path)
    {
        if (path.StartsWith(WindowsFolder, StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(AppDataRoaming, StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith(AppDataLocal, StringComparison.OrdinalIgnoreCase)) return true;

        bool underProgramFiles =
            path.StartsWith(ProgramFiles, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(ProgramFilesX86, StringComparison.OrdinalIgnoreCase);

        // Program Files alatt csak akkor engedjuk at, ha ismert jatek-platform
        // almappaban van - minden mas (Office, bongeszok, launcher-kliensek,
        // driverek, stb.) kizarva.
        if (underProgramFiles && !IsKnownGamePlatformPath(path))
            return true;

        return false;
    }

    #endregion

    #region Excluded Exe Name Patterns

    // Ha egy exe fajlneve (kiterjesztes nelkul, kisbetuvel) TARTALMAZZA
    // valamelyik mintat, akkor nem tekintjuk a jatek fo exe-jenek, meg akkor
    // sem, ha nagy meretu (pl. egy beagyazott DirectX/VCRedist telepito
    // tobb tiz MB is lehet). Meret-fuggetlen: egy 2 MB-os Unity-s jatek
    // exe-je pontosan ugy elfogadhato, mint egy 200 MB-os AAA jatek exe-je.
    private static readonly string[] ExcludedExeNamePatterns = new[]
    {
        "uninstall", "unins000", "unins001", "unwise",
        "setup", "install",
        "vcredist", "vc_redist", "vcruntime",
        "directx", "dxsetup", "dxwebsetup",
        "dotnetfx", "dotnet-runtime", "windowsdesktop-runtime",
        "crashreport", "crashpad", "crashhandler", "unitycrashhandler",
        "redist", "prerequisites", "prereq",
        "updater", "update_checker", "autoupdate",
        "eossdk", "easyanticheat_setup", "battleye_setup",
        "vcpp", "oalinst"
    };

    // Mappanevek, amik ha barhol szerepelnek a teljes elerhesi utban,
    // telepito/futtatokornyezet mappara utalnak (pl. Steam sajat
    // "Steamworks Common Redistributables" alkalmazasa a _CommonRedist alatt).
    private static readonly string[] ExcludedPathSegments = new[]
    {
        "_commonredist", "commonredist", "_redist", "redistributables"
    };

    /// <summary>
    /// True, ha a fajlnev VAGY a teljes eleresi ut alapjan valoszinuleg NEM
    /// a jatek fo exe-je, hanem valamilyen telepito/segedprogram/
    /// futtatokornyezet.
    /// </summary>
    internal static bool IsLikelyNonGameExecutable(string filePath)
    {
        var nameOnly = Path.GetFileNameWithoutExtension(filePath).ToLowerInvariant();
        if (ExcludedExeNamePatterns.Any(pattern => nameOnly.Contains(pattern)))
            return true;

        var lowerPath = filePath.ToLowerInvariant();
        if (ExcludedPathSegments.Any(segment => lowerPath.Contains(segment)))
            return true;

        return false;
    }

    #endregion

    // -------------------------------------------------------------------------
    // PUBLIC API
    // -------------------------------------------------------------------------

    /// <summary>
    /// Runs all instant scans (Start Menu, Steam, Epic, Ubisoft, EA, GOG, Battle.net).
    /// Returns in seconds — no waiting.
    /// </summary>
    public async Task<List<GameScanResult>> RunInstantScansAsync(IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            var results = new List<GameScanResult>();

            // Key = exe path (case-insensitive), Value = best result found so far
            // When the same exe is found by two scanners, the one with lower priority
            // number wins and its name replaces the worse one
            var seen = new Dictionary<string, GameScanResult>(StringComparer.OrdinalIgnoreCase);

            // Run highest priority sources first so their names are already in seen
            // when lower priority sources find the same exe
            progress?.Report("Reading Steam manifests...");
            var steamResults = SafeScan(GetFromSteam, "Steam");
            Debug.WriteLine($"[RunInstantScansAsync] Steam talalatok: {steamResults.Count}");
            AddRange(results, seen, steamResults);

            progress?.Report("Reading Epic Games manifests...");
            var epicResults = SafeScan(GetFromEpic, "Epic");
            Debug.WriteLine($"[RunInstantScansAsync] Epic talalatok: {epicResults.Count}");
            AddRange(results, seen, epicResults);

            progress?.Report("Reading Start Menu shortcuts...");
            var startMenuResults = SafeScan(GetFromStartMenu, "StartMenu");
            Debug.WriteLine($"[RunInstantScansAsync] StartMenu talalatok: {startMenuResults.Count}");
            AddRange(results, seen, startMenuResults);

            // A Registry-scanner (GetFromRegistry) SZANDEKOSAN ki van kapcsolva:
            // minden telepitett programot felvett (Office, Chrome, driverek, stb.),
            // nem csak jatekokat, es valodi extra jatekot nem hozott a StartMenu/
            // Steam/Epic/GOG/Ubisoft/EA/BattleNet scannerekhez kepest.
            // A metodus megmaradt lent, ha kesobb megis kellene.

            progress?.Report("Reading Ubisoft Connect...");
            var ubisoftResults = SafeScan(GetFromUbisoft, "Ubisoft");
            Debug.WriteLine($"[RunInstantScansAsync] Ubisoft talalatok: {ubisoftResults.Count}");
            AddRange(results, seen, ubisoftResults);

            progress?.Report("Reading EA App...");
            var eaResults = SafeScan(GetFromEA, "EA");
            Debug.WriteLine($"[RunInstantScansAsync] EA talalatok: {eaResults.Count}");
            AddRange(results, seen, eaResults);

            progress?.Report("Reading GOG Galaxy...");
            var gogResults = SafeScan(GetFromGOG, "GOG");
            Debug.WriteLine($"[RunInstantScansAsync] GOG talalatok: {gogResults.Count}");
            AddRange(results, seen, gogResults);

            progress?.Report("Reading Battle.net...");
            var battleNetResults = SafeScan(GetFromBattleNet, "BattleNet");
            Debug.WriteLine($"[RunInstantScansAsync] BattleNet talalatok: {battleNetResults.Count}");
            AddRange(results, seen, battleNetResults);

            progress?.Report($"Done — found {results.Count} games.");
            Debug.WriteLine($"[RunInstantScansAsync] OSSZESEN (dedup utan): {results.Count}");
            return results;
        });
    }

    /// <summary>
    /// Scans specific folders for .exe files (for pirated / portable games).
    /// Call this only when the user explicitly requests it.
    /// </summary>
    public async Task<List<GameScanResult>> ScanFoldersAsync(
        IEnumerable<string> folders,
        IProgress<string>? progress = null)
    {
        return await Task.Run(() =>
        {
            var results = new List<GameScanResult>();
            var seen = new Dictionary<string, GameScanResult>(StringComparer.OrdinalIgnoreCase);

            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder)) continue;

                progress?.Report($"Scanning {folder}...");

                try
                {
                    foreach (var result in EnumerateSafe(folder))
                    {
                        AddRange(results, seen, new List<GameScanResult> { result });
                        progress?.Report($"Found: {result.GameName}");
                    }
                }
                catch (Exception ex)
                {
                    // Egy elérhetetlen mappa ne állítsa le az egész szkennelést.
                    Debug.WriteLine($"[ScanFoldersAsync] HIBA a(z) {folder} szkennelésekor: {ex.Message}");
                }
            }

            Debug.WriteLine($"[ScanFoldersAsync] Osszesen talalt exe: {results.Count}");
            return results;
        });
    }

    // -------------------------------------------------------------------------
    // 1. START MENU SHORTCUTS
    // -------------------------------------------------------------------------

    private static List<GameScanResult> GetFromStartMenu()
    {
        var results = new List<GameScanResult>();

        var roots = new[]
        {
            // System-wide — apps installed for all users (admin installs)
            @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs",

            // Per-user — apps installed just for the current user
            // Using SpecialFolder instead of hardcoding C:\Users\Atesz\...
            // so it works on every machine regardless of username
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs")
        };

        // Egyetlen WScript.Shell példány az összes parancsikonhoz (nem pedig .lnk-nként újat).
        var shell = CreateShell();

        foreach (var root in roots)
        {
            Debug.WriteLine($"[GetFromStartMenu] Mappa: {root} | letezik: {Directory.Exists(root)}");
            if (!Directory.Exists(root)) continue;

            var lnkFiles = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToList();
            Debug.WriteLine($"[GetFromStartMenu] {root} alatt talalt .lnk fajlok: {lnkFiles.Count}");

            foreach (var lnk in lnkFiles)
            {
                try
                {
                    var target = ResolveShortcut(shell, lnk);

                    if (string.IsNullOrEmpty(target)) continue;
                    if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(target)) continue;

                    if (IsUnderExcludedRoot(target))
                    {
                        Debug.WriteLine($"[GetFromStartMenu] Kihagyva (tiltott mappa): {target}");
                        continue;
                    }

                    if (IsLikelyNonGameExecutable(target))
                    {
                        Debug.WriteLine($"[GetFromStartMenu] Kihagyva (nev alapjan telepito/segedprogram): {target}");
                        continue;
                    }

                    results.Add(new GameScanResult
                    {
                        ExePath = target,
                        GameName = Path.GetFileNameWithoutExtension(lnk),
                        Source = "StartMenu"
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[StartMenu scan] Skipped {lnk}: {ex.Message}");
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Resolves a .lnk shortcut file to its target path.
    /// Uses the built-in WScript.Shell COM object — no NuGet package needed.
    /// </summary>
    private static object? CreateShell()
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            return type == null ? null : Activator.CreateInstance(type);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveShortcut(object? shell, string lnkPath)
    {
        if (shell == null) return null;

        object? shortcut = null;
        try
        {
            shortcut = shell.GetType().InvokeMember(
                "CreateShortcut",
                System.Reflection.BindingFlags.InvokeMethod,
                null, shell, new object[] { lnkPath });

            if (shortcut == null) return null;

            return shortcut.GetType().InvokeMember(
                "TargetPath",
                System.Reflection.BindingFlags.GetProperty,
                null, shortcut, null) as string;
        }
        catch
        {
            return null;
        }
        finally
        {
            if (shortcut != null && System.Runtime.InteropServices.Marshal.IsComObject(shortcut))
                System.Runtime.InteropServices.Marshal.ReleaseComObject(shortcut);
        }
    }

    // -------------------------------------------------------------------------
    // 2. REGISTRY UNINSTALL KEYS
    // -------------------------------------------------------------------------
    //
    // MEGJEGYZES: ezt a scannert a RunInstantScansAsync jelenleg NEM hivja meg,
    // mert minden telepitett programot felvesz (nem csak jatekokat). A metodus
    // megmaradt, arra az esetre, ha kesobb megis szukseg lenne ra (pl. szigorubb
    // szures mellett).

    private static List<GameScanResult> GetFromRegistry()
    {
        var results = new List<GameScanResult>();

        var keys = new[]
        {
            // 64-bit programs
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            // 32-bit programs running on 64-bit Windows (WOW64 compatibility layer)
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        foreach (var key in keys)
        {
            using var root = Registry.LocalMachine.OpenSubKey(key);
            if (root == null)
            {
                Debug.WriteLine($"[GetFromRegistry] Nem sikerult megnyitni: {key}");
                continue;
            }

            var subKeyNames = root.GetSubKeyNames();
            Debug.WriteLine($"[GetFromRegistry] {key} alatt talalt bejegyzesek: {subKeyNames.Length}");

            foreach (var subKeyName in subKeyNames)
            {
                try
                {
                    using var sub = root.OpenSubKey(subKeyName);
                    if (sub == null) continue;

                    var installLocation = sub.GetValue("InstallLocation")?.ToString();
                    var displayName = sub.GetValue("DisplayName")?.ToString();

                    if (string.IsNullOrEmpty(installLocation)) continue;
                    if (!Directory.Exists(installLocation)) continue;
                    if (IsUnderExcludedRoot(installLocation)) continue;

                    // Only scan the top-level install folder (not recursive)
                    // since InstallLocation points directly to the game folder
                    var exe = Directory.EnumerateFiles(installLocation, "*.exe")
                        .Where(f => !IsLikelyNonGameExecutable(f))
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .FirstOrDefault();

                    if (exe == null) continue;

                    results.Add(new GameScanResult
                    {
                        ExePath = exe.FullName,
                        GameName = displayName ?? Path.GetFileNameWithoutExtension(exe.Name),
                        Source = "Registry"
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GetFromRegistry] HIBA a(z) {subKeyName} feldolgozasakor: {ex.Message}");
                }
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // 3. STEAM MANIFESTS
    // -------------------------------------------------------------------------

    private static List<GameScanResult> GetFromSteam()
    {
        var results = new List<GameScanResult>();

        var foundLibraries = FindSteamLibraries();
        Debug.WriteLine($"[GetFromSteam] Talalt Steam library-k: {string.Join(", ", foundLibraries)}");

        foreach (var library in foundLibraries)
        {
            var steamAppsPath = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamAppsPath))
            {
                Debug.WriteLine($"[GetFromSteam] Nem letezik: {steamAppsPath}");
                continue;
            }

            var manifests = Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf").ToList();
            Debug.WriteLine($"[GetFromSteam] {steamAppsPath} alatt talalt manifest fajlok: {manifests.Count}");

            foreach (var manifest in manifests)
            {
                try
                {
                    var lines = File.ReadAllLines(manifest);

                    // Steam's .acf format: lines look like   "name"    "The Witcher 3"
                    // Split by " and take index [3] to get the value
                    var name = lines
                        .FirstOrDefault(l => l.TrimStart().StartsWith("\"name\""))
                        ?.Split('"')[3];

                    var installDir = lines
                        .FirstOrDefault(l => l.TrimStart().StartsWith("\"installdir\""))
                        ?.Split('"')[3];

                    Debug.WriteLine($"[GetFromSteam] Manifest: {Path.GetFileName(manifest)} | name={name} | installdir={installDir}");

                    if (string.IsNullOrEmpty(installDir)) continue;

                    var gamePath = Path.Combine(steamAppsPath, "common", installDir);
                    if (!Directory.Exists(gamePath))
                    {
                        Debug.WriteLine($"[GetFromSteam] gamePath nem letezik: {gamePath}");
                        continue;
                    }

                    var candidates = Directory.EnumerateFiles(gamePath, "*.exe", SearchOption.AllDirectories)
                        .Where(f => !IsLikelyNonGameExecutable(f))
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .ToList();

                    Debug.WriteLine($"[GetFromSteam] {gamePath} alatt (szuro utan) marad exe-k: " +
                        string.Join(", ", candidates.Select(f => $"{f.Name} ({f.Length / 1024} KB)")));

                    var exe = candidates.FirstOrDefault();

                    if (exe == null)
                    {
                        Debug.WriteLine($"[GetFromSteam] Nincs hasznalhato exe ehhez: {installDir}");
                        continue;
                    }

                    results.Add(new GameScanResult
                    {
                        ExePath = exe.FullName,
                        GameName = name ?? installDir,
                        Source = "Steam",
                        InstallRoot = gamePath
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GetFromSteam] HIBA a(z) {manifest} feldolgozasakor: {ex}");
                }
            }
        }

        return results;
    }

    public static List<string> FindSteamLibraries()
    {
        var libraries = new List<string>();

        // Duplikátum nélkül (a libraryfolders.vdf az alap Steam mappát is felsorolja),
        // különben kétszer szkennelnénk és két FileSystemWatcher figyelné ugyanazt.
        void AddLibrary(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
                if (Directory.Exists(full) && !libraries.Contains(full, StringComparer.OrdinalIgnoreCase))
                    libraries.Add(full);
            }
            catch { }
        }

        // A Steam nem feltétlenül az alap helyen van: a registry tudja a valódi útvonalat.
        try
        {
            AddLibrary(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FindSteamLibraries] Registry hiba: {ex.Message}");
        }

        AddLibrary(@"C:\Program Files (x86)\Steam");
        Debug.WriteLine($"[FindSteamLibraries] Steam gyökér(ek): {string.Join(", ", libraries)}");

        // A libraryfolders.vdf az összes library útvonalát tartalmazza (D:\, E:\ stb.).
        foreach (var steamRoot in libraries.ToList())
        {
            var vdfPath = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(vdfPath)) continue;

            try
            {
                foreach (var line in File.ReadAllLines(vdfPath))
                {
                    // Sorok: "path"    "D:\\Games\\Steam"
                    if (!line.TrimStart().StartsWith("\"path\"")) continue;

                    var parts = line.Split('"');
                    if (parts.Length < 4) continue;

                    var path = parts[3].Replace(@"\\", @"\");
                    Debug.WriteLine($"[FindSteamLibraries] vdf-ben talalt path: {path} | letezik: {Directory.Exists(path)}");
                    AddLibrary(path);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[FindSteamLibraries] HIBA a vdf feldolgozasakor: {ex}");
            }
        }

        return libraries;
    }

    // -------------------------------------------------------------------------
    // 4. EPIC GAMES MANIFESTS
    // -------------------------------------------------------------------------

    private static List<GameScanResult> GetFromEpic()
    {
        var results = new List<GameScanResult>();

        var manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
        Debug.WriteLine($"[GetFromEpic] Manifests mappa letezik: {Directory.Exists(manifestsPath)} ({manifestsPath})");
        if (!Directory.Exists(manifestsPath)) return results;

        var itemFiles = Directory.EnumerateFiles(manifestsPath, "*.item").ToList();
        Debug.WriteLine($"[GetFromEpic] Talalt .item fajlok: {itemFiles.Count}");

        foreach (var file in itemFiles)
        {
            try
            {
                var json = File.ReadAllText(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var installPath = root.TryGetProperty("InstallLocation", out var loc)
                    ? loc.GetString() : null;
                var displayName = root.TryGetProperty("DisplayName", out var name)
                    ? name.GetString() : null;

                // Epic provides the exact exe to launch — most accurate method
                var launchExe = root.TryGetProperty("LaunchExecutable", out var exe)
                    ? exe.GetString() : null;

                Debug.WriteLine($"[GetFromEpic] {Path.GetFileName(file)} | name={displayName} | installPath={installPath} | launchExe={launchExe}");

                if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;

                string? exePath = null;

                // Epic pontosan megmondja, melyik exe-t kell inditani - ez a
                // legmegbizhatobb forras, itt nincs is szukseg nev-alapu szuresre.
                if (!string.IsNullOrEmpty(launchExe))
                {
					var fullPath = Path.GetFullPath(Path.Combine(installPath, launchExe));
                    if (File.Exists(fullPath))
                        exePath = fullPath;
                }

                // Fallback, ha a LaunchExecutable hianyzik vagy egy wrapper-re mutat
                exePath ??= Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !IsLikelyNonGameExecutable(f))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault()?.FullName;

                if (exePath == null)
                {
                    Debug.WriteLine($"[GetFromEpic] Nem talalhato hasznalhato exe ehhez: {displayName}");
                    continue;
                }

                results.Add(new GameScanResult
                {
                    ExePath = exePath,
                    GameName = displayName ?? Path.GetFileNameWithoutExtension(exePath),
                    Source = "Epic",
                    InstallRoot = installPath
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetFromEpic] HIBA a(z) {file} feldolgozasakor: {ex.Message}");
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // 5. UBISOFT CONNECT
    // -------------------------------------------------------------------------

    private static List<GameScanResult> GetFromUbisoft()
    {
        var results = new List<GameScanResult>();

        var root = @"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games";
        Debug.WriteLine($"[GetFromUbisoft] Mappa letezik: {Directory.Exists(root)} ({root})");
        if (!Directory.Exists(root)) return results;

        var gameFolders = Directory.EnumerateDirectories(root).ToList();
        Debug.WriteLine($"[GetFromUbisoft] Talalt almappak: {gameFolders.Count}");

        foreach (var gameFolder in gameFolders)
        {
            try
            {
                var exe = Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !IsLikelyNonGameExecutable(f))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault();

                if (exe == null)
                {
                    Debug.WriteLine($"[GetFromUbisoft] Nincs hasznalhato exe ehhez: {gameFolder}");
                    continue;
                }

                results.Add(new GameScanResult
                {
                    ExePath = exe.FullName,
                    GameName = Path.GetFileName(gameFolder),
                    Source = "Ubisoft",
                    InstallRoot = gameFolder
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetFromUbisoft] HIBA a(z) {gameFolder} feldolgozasakor: {ex.Message}");
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // 6. EA APP (and old Origin)
    // -------------------------------------------------------------------------

    private static List<GameScanResult> GetFromEA()
    {
        var results = new List<GameScanResult>();

        var roots = new[]
        {
            @"C:\Program Files\EA Games",
            @"C:\Program Files (x86)\Origin Games",
        };

        foreach (var root in roots)
        {
            Debug.WriteLine($"[GetFromEA] Mappa letezik: {Directory.Exists(root)} ({root})");
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            var gameFolders = Directory.EnumerateDirectories(root).ToList();
            Debug.WriteLine($"[GetFromEA] {root} alatt talalt almappak: {gameFolders.Count}");

            foreach (var gameFolder in gameFolders)
            {
                try
                {
                    var exe = Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.AllDirectories)
                        .Where(f => !IsLikelyNonGameExecutable(f))
                        .Select(f => new FileInfo(f))
                        .OrderByDescending(f => f.Length)
                        .FirstOrDefault();

                    if (exe == null)
                    {
                        Debug.WriteLine($"[GetFromEA] Nincs hasznalhato exe ehhez: {gameFolder}");
                        continue;
                    }

                    results.Add(new GameScanResult
                    {
                        ExePath = exe.FullName,
                        GameName = Path.GetFileName(gameFolder),
                        Source = "EA",
                        InstallRoot = gameFolder
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[GetFromEA] HIBA a(z) {gameFolder} feldolgozasakor: {ex.Message}");
                }
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // 7. GOG GALAXY
    // -------------------------------------------------------------------------

    private static List<GameScanResult> GetFromGOG()
    {
        var results = new List<GameScanResult>();

        var root = @"C:\Program Files (x86)\GOG Galaxy\Games";
        Debug.WriteLine($"[GetFromGOG] Mappa letezik: {Directory.Exists(root)} ({root})");
        if (!Directory.Exists(root)) return results;

        var gameFolders = Directory.EnumerateDirectories(root).ToList();
        Debug.WriteLine($"[GetFromGOG] Talalt almappak: {gameFolders.Count}");

        foreach (var gameFolder in gameFolders)
        {
            try
            {
                var exe = Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !IsLikelyNonGameExecutable(f))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault();

                if (exe == null)
                {
                    Debug.WriteLine($"[GetFromGOG] Nincs hasznalhato exe ehhez: {gameFolder}");
                    continue;
                }

                results.Add(new GameScanResult
                {
                    ExePath = exe.FullName,
                    GameName = Path.GetFileName(gameFolder),
                    Source = "GOG",
                    InstallRoot = gameFolder
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetFromGOG] HIBA a(z) {gameFolder} feldolgozasakor: {ex.Message}");
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // 8. BATTLE.NET
    // -------------------------------------------------------------------------

    private static List<GameScanResult> GetFromBattleNet()
    {
        var results = new List<GameScanResult>();

        var roots = new[]
        {
            @"C:\Program Files (x86)\Overwatch",
            @"C:\Program Files\Overwatch 2",
            @"C:\Program Files (x86)\World of Warcraft",
            @"C:\Program Files (x86)\Diablo IV",
            @"C:\Program Files (x86)\Hearthstone",
            @"C:\Program Files (x86)\StarCraft II",
            @"C:\Program Files (x86)\Heroes of the Storm",
            @"C:\Program Files (x86)\Call of Duty",
        };

        foreach (var root in roots)
        {
            Debug.WriteLine($"[GetFromBattleNet] Mappa letezik: {Directory.Exists(root)} ({root})");
        }

        foreach (var root in roots.Where(Directory.Exists))
        {
            try
            {
                var exe = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
                    .Where(f => !IsLikelyNonGameExecutable(f))
                    .Select(f => new FileInfo(f))
                    .OrderByDescending(f => f.Length)
                    .FirstOrDefault();

                if (exe == null)
                {
                    Debug.WriteLine($"[GetFromBattleNet] Nincs hasznalhato exe ehhez: {root}");
                    continue;
                }

                results.Add(new GameScanResult
                {
                    ExePath = exe.FullName,
                    GameName = Path.GetFileName(root),
                    Source = "BattleNet",
                    InstallRoot = root
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[GetFromBattleNet] HIBA a(z) {root} feldolgozasakor: {ex.Message}");
            }
        }

        return results;
    }

    // -------------------------------------------------------------------------
    // 9. SAFE RECURSIVE SCAN (for pirated / portable games)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Egy adott gyoker mappa (pl. D:\Games) kozvetlen almappait KULON-KULON
    /// jateknak tekinti, es mindegyikbol csak EGY, legvalloszinubb fo exe-t
    /// valaszt ki (lasd FindBestGameExecutable) - igy egy tobb-exes jatekmappabol
    /// (pl. Content Manager + acServer + acs.exe egy Assetto Corsa mappaban)
    /// nem lesz tobb kulon talalat.
    /// </summary>
    private IEnumerable<GameScanResult> EnumerateSafe(string root)
    {
        if (ShouldExclude(root)) yield break;

        // Kozvetlenul a gyoker alatt levo exe-k - ha valaki egy mappaba
        // egyetlen exe-t masol be (portable jatek almappa nelkul), azt is
        // onallo talalatkent vegyuk fel.
        foreach (var f in SafeEnumerateFiles(root))
        {
            if (IsLikelyNonGameExecutable(f)) continue;
            yield return new GameScanResult
            {
                ExePath = f,
                GameName = Path.GetFileNameWithoutExtension(f),
                Source = "Scan"
            };
        }

        // Minden kozvetlen almappa = egy jatek. Csak a legvalloszinubb fo
        // exe-t vesszuk fel belole, nem a mappafaban talalhato OSSZES exe-t.
        foreach (var subDir in SafeEnumerateDirectories(root))
        {
            if (ShouldExclude(subDir)) continue;

            var bestExe = FindBestGameExecutable(subDir);
            if (bestExe == null) continue;

            yield return new GameScanResult
            {
                ExePath = bestExe.FullName,
                GameName = Path.GetFileName(subDir),
                Source = "Scan",
                InstallRoot = subDir
            };
        }
    }

    // -------------------------------------------------------------------------
    // 9b. FO EXE KIVALASZTASA TOBB JELOLT KOZUL (pontozasos rendszer)
    // -------------------------------------------------------------------------

    /// <summary>
    /// Rekurzivan osszegyujti egy mappafaban talalhato, valoszinuleg jatek-exe
    /// jeloltjeit, majd tobb jel kombinaciojaval (nev-egyezes, beagyazott
    /// metaadat, PE subsystem, Unreal Engine konvenciok, meret) kivalasztja
    /// a legvalloszinubb fo exe-t. Sokkal megbizhatobb, mint a puszta
    /// fajlmeret alapjan valasztas.
    /// </summary>
    private static FileInfo? FindBestGameExecutable(string folder)
    {
        var candidates = new List<FileInfo>();
        CollectExeCandidates(folder, candidates);

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1) return candidates[0];

        var folderName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar));
        var normalizedFolderName = NormalizeForComparison(folderName);

        return candidates
            .Select(f => new { File = f, Score = ScoreExecutable(f, normalizedFolderName) })
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.File.Length)
            .First()
            .File;
    }

    private const int MaxExeSearchDepth = 8;

    private static void CollectExeCandidates(string folder, List<FileInfo> candidates, int depth = 0)
    {
        if (depth > MaxExeSearchDepth) return;
        if (ShouldExclude(folder)) return;

        foreach (var f in SafeEnumerateFiles(folder))
        {
            if (IsLikelyNonGameExecutable(f)) continue;
            candidates.Add(new FileInfo(f));
        }

        foreach (var dir in SafeEnumerateDirectories(folder))
            CollectExeCandidates(dir, candidates, depth + 1);
    }

    // Kulcsszavak, amik a beagyazott ProductName/FileDescription metaadatban
    // arra utalnak, hogy az exe NEM a jatek fo, jatszhato binarisa.
    private static readonly string[] NonPrimaryMetadataKeywords = new[]
    {
        "dedicated server", "server", "launcher", "crash", "report",
        "updater", "uninstall", "setup", "benchmark", "editor",
        "redistributable", "sdk", "diagnostic", "config", "tool"
    };

    private static int ScoreExecutable(FileInfo file, string normalizedFolderName)
    {
        int score = 0;
        var nameOnly = Path.GetFileNameWithoutExtension(file.Name);

        // 1. Fajlnev egyezik a mappa nevevel - eros jel.
        if (NormalizeForComparison(nameOnly) == normalizedFolderName)
            score += 100;

        // 2. Beagyazott metaadat (ProductName/FileDescription) - meg erosebb
        //    jel, mert ez tudatosan beallitott, hivatalos adat, es kiszuri
        //    az olyan 3rd party eszkozoket is, mint a Content Manager.
        string? productName = null;
        string? fileDescription = null;
        try
        {
            var info = FileVersionInfo.GetVersionInfo(file.FullName);
            productName = info.ProductName;
            fileDescription = info.FileDescription;
        }
        catch
        {
            // Nincs verzio-info beagyazva - sok portable/indie jateknal
            // ez teljesen normalis, nem hiba.
        }

        if (!string.IsNullOrWhiteSpace(productName) &&
            NormalizeForComparison(productName!) == normalizedFolderName)
            score += 120;

        var combinedMeta = $"{productName} {fileDescription}".ToLowerInvariant();
        if (NonPrimaryMetadataKeywords.Any(k => combinedMeta.Contains(k)))
            score -= 80;

        // 3. Unreal Engine: a "*-Win64-Shipping.exe" a motor tenyleges
        //    binarisa, de a hivatalos inditasi mod a suffix nelkuli wrapper
        //    exe (pl. "Game.exe"), ha az letezik a mappaban.
        var lowerName = nameOnly.ToLowerInvariant();
        if (lowerName.Contains("-win64-shipping") ||
            lowerName.Contains("-win32-shipping") ||
            lowerName.Contains("-wingdk-shipping"))
            score -= 40;

        // 4. PE subsystem: a jatszhato jatek szinte mindig sajat ablakot nyit
        //    (GUI subsystem), a dedicated server / CLI eszkozok altalaban
        //    konzolosak (CUI subsystem).
        var subsystem = TryReadPeSubsystem(file.FullName);
        if (subsystem == PeSubsystem.WindowsGui) score += 20;
        else if (subsystem == PeSubsystem.WindowsCui) score -= 30;

        // 5. Meret, mint leggyengebb, utolso jelzes - log-skalan, hogy ne
        //    tudjon onmagaban felulirni minden mas, erosebb jelet.
        score += (int)Math.Log(Math.Max(file.Length, 1), 2);

        return score;
    }

    /// <summary>
    /// Csak betuket/szamokat hagy meg, kisbetusit - igy pl. a "Slime Rancher 2"
    /// mappanev es a "SlimeRancher2" fajlnev/ProductName is egyezni fog.
    /// </summary>
    private static string NormalizeForComparison(string name)
    {
        return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private enum PeSubsystem : ushort
    {
        WindowsGui = 2,
        WindowsCui = 3,
    }

    /// <summary>
    /// Kezzel kiolvassa a PE fejlecbol a Subsystem mezot (kulso csomag
    /// nelkul). A Subsystem mezo pontosan 68 bajttal az Optional Header
    /// eleje utan talalhato, es ez az offset veletlenul azonos PE32 (32 bit)
    /// es PE32+ (64 bit) eseten is - igy nem kell kulon kezelni oket.
    /// </summary>
    private static PeSubsystem? TryReadPeSubsystem(string exePath)
    {
        try
        {
            using var stream = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            if (stream.Length < 0x40) return null;

            stream.Seek(0x3C, SeekOrigin.Begin);
            int peHeaderOffset = reader.ReadInt32();

            if (peHeaderOffset <= 0 || peHeaderOffset + 4 + 20 + 68 + 2 > stream.Length)
                return null;

            stream.Seek(peHeaderOffset, SeekOrigin.Begin);
            uint peSignature = reader.ReadUInt32();
            if (peSignature != 0x00004550) return null; // "PE\0\0"

            // COFF File Header = 20 bajt, utana az Optional Header - a
            // Subsystem mezo annak +68 bajt offszetjenel van.
            stream.Seek(peHeaderOffset + 4 + 20 + 68, SeekOrigin.Begin);
            ushort subsystem = reader.ReadUInt16();

            return Enum.IsDefined(typeof(PeSubsystem), subsystem)
                ? (PeSubsystem)subsystem
                : null;
        }
        catch
        {
            return null;
        }
    }

    // Ezek a mappák SOHA nem játékok (a C:\ mélyszkennelésnél a Users és a ProgramData
    // közvetlen almappaként "játéknak" látszana, és az alattuk futó rendszerfolyamatok
    // miatt a játék sessionje sosem záródna le).
    private static readonly HashSet<string> NeverGameFolders = BuildNeverGameFolders();

    private static HashSet<string> BuildNeverGameFolders()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path))
                set.Add(Path.TrimEndingDirectorySeparator(path));
        }

        Add(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)); // C:\ProgramData

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            Add(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(profile)));      // C:\Users

        return set;
    }

    private static bool ShouldExclude(string path)
    {
        if (NeverGameFolders.Contains(Path.TrimEndingDirectorySeparator(path))) return true;
        if (ExcludedFolders.Contains(path)) return true;
        if (IsUnderExcludedRoot(path)) return true;

        if (path.Contains(@"\AppData\", StringComparison.OrdinalIgnoreCase))
        {
            var folderName = Path.GetFileName(path);
            if (ExcludedAppDataFolders.Contains(folderName)) return true;
        }

        return false;
    }

    // -------------------------------------------------------------------------
    // HELPERS
    // -------------------------------------------------------------------------

    /// <summary>
    /// Egy gyors scanner futtatása úgy, hogy a hibája ne vigye magával a többit
    /// (és az App.OnStartup-ot sem).
    /// </summary>
    private static List<GameScanResult> SafeScan(Func<List<GameScanResult>> scanner, string name)
    {
        try
        {
            return scanner();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SafeScan] {name} scanner hiba: {ex}");
            return new List<GameScanResult>();
        }
    }

    // A Directory.EnumerateFiles(path, pattern) alapértelmezésben DOBJA a UnauthorizedAccessException-t
    // (pl. D:\System Volume Information), és ez nem a hívásnál, hanem a foreach-nél derül ki -
    // ezért a try/catch-nek az ENUMERÁLÁST kell körbevennie. A rejtett/rendszer mappákat kihagyjuk.
    private static readonly EnumerationOptions SafeEnumOptions = new()
    {
        IgnoreInaccessible = true,
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    private static List<string> SafeEnumerateFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "*.exe", SafeEnumOptions).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    private static List<string> SafeEnumerateDirectories(string folder)
    {
        try { return Directory.EnumerateDirectories(folder, "*", SafeEnumOptions).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// Adds results to the list, deduplicating by exe path.
    /// When two scanners find the same exe, the one with the lower priority
    /// number wins and its name replaces the worse one.
    /// </summary>
    private static void AddRange(
        List<GameScanResult> target,
        Dictionary<string, GameScanResult> seen,
        List<GameScanResult> source)
    {
        foreach (var item in source)
        {
            if (seen.TryGetValue(item.ExePath, out var existing))
            {
                var newPriority = SourcePriority.GetValueOrDefault(item.Source, 99);
                var existingPriority = SourcePriority.GetValueOrDefault(existing.Source, 99);

                // Ha a mar tarolt talalatnak nincs telepitesi mappaja, az ujabbtol atvesszuk.
                if (string.IsNullOrEmpty(existing.InstallRoot))
                    existing.InstallRoot = item.InstallRoot;

                // New source is more trustworthy — upgrade the name
                // The object reference in seen and target point to the same instance
                // so mutating existing.GameName updates both automatically
                if (newPriority < existingPriority)
                {
                    existing.GameName = item.GameName;
                    existing.Source = item.Source;
                    if (!string.IsNullOrEmpty(item.InstallRoot))
                        existing.InstallRoot = item.InstallRoot;
                }
            }
            else
            {
                seen[item.ExePath] = item;
                target.Add(item);
            }
        }
    }
}


