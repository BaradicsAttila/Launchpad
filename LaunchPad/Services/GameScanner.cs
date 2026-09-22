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
	private static bool IsLikelyNonGameExecutable(string filePath)
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
			var steamResults = GetFromSteam();
			Debug.WriteLine($"[RunInstantScansAsync] Steam talalatok: {steamResults.Count}");
			AddRange(results, seen, steamResults);

			progress?.Report("Reading Epic Games manifests...");
			var epicResults = GetFromEpic();
			Debug.WriteLine($"[RunInstantScansAsync] Epic talalatok: {epicResults.Count}");
			AddRange(results, seen, epicResults);

			progress?.Report("Reading Start Menu shortcuts...");
			var startMenuResults = GetFromStartMenu();
			Debug.WriteLine($"[RunInstantScansAsync] StartMenu talalatok: {startMenuResults.Count}");
			AddRange(results, seen, startMenuResults);

			// A Registry-scanner (GetFromRegistry) SZANDEKOSAN ki van kapcsolva:
			// minden telepitett programot felvett (Office, Chrome, driverek, stb.),
			// nem csak jatekokat, es valodi extra jatekot nem hozott a StartMenu/
			// Steam/Epic/GOG/Ubisoft/EA/BattleNet scannerekhez kepest.
			// A metodus megmaradt lent, ha kesobb megis kellene.

			progress?.Report("Reading Ubisoft Connect...");
			var ubisoftResults = GetFromUbisoft();
			Debug.WriteLine($"[RunInstantScansAsync] Ubisoft talalatok: {ubisoftResults.Count}");
			AddRange(results, seen, ubisoftResults);

			progress?.Report("Reading EA App...");
			var eaResults = GetFromEA();
			Debug.WriteLine($"[RunInstantScansAsync] EA talalatok: {eaResults.Count}");
			AddRange(results, seen, eaResults);

			progress?.Report("Reading GOG Galaxy...");
			var gogResults = GetFromGOG();
			Debug.WriteLine($"[RunInstantScansAsync] GOG talalatok: {gogResults.Count}");
			AddRange(results, seen, gogResults);

			progress?.Report("Reading Battle.net...");
			var battleNetResults = GetFromBattleNet();
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

				foreach (var exe in EnumerateSafe(folder))
				{
					var result = new GameScanResult
					{
						ExePath = exe,
						GameName = Path.GetFileNameWithoutExtension(exe),
						Source = "Scan"
					};

					AddRange(results, seen, new List<GameScanResult> { result });
					progress?.Report($"Found: {Path.GetFileName(exe)}");
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
					var target = ResolveShortcut(lnk);

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
	private static string? ResolveShortcut(string lnkPath)
	{
		try
		{
			var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
			var shortcut = shell!.GetType().InvokeMember(
				"CreateShortcut",
				System.Reflection.BindingFlags.InvokeMethod,
				null, shell, new object[] { lnkPath });

			var target = shortcut!.GetType().InvokeMember(
				"TargetPath",
				System.Reflection.BindingFlags.GetProperty,
				null, shortcut, null) as string;

			return target;
		}
		catch
		{
			return null;
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
						Source = "Steam"
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

		var defaultSteam = @"C:\Program Files (x86)\Steam";
		Debug.WriteLine($"[FindSteamLibraries] Alapertelmezett Steam mappa letezik: {Directory.Exists(defaultSteam)} ({defaultSteam})");

		if (Directory.Exists(defaultSteam))
			libraries.Add(defaultSteam);

		// Steam stores all library paths in this file
		// This handles users who have games on D:\, E:\, etc.
		var vdfPath = Path.Combine(defaultSteam, @"steamapps\libraryfolders.vdf");
		Debug.WriteLine($"[FindSteamLibraries] libraryfolders.vdf letezik: {File.Exists(vdfPath)} ({vdfPath})");

		if (!File.Exists(vdfPath)) return libraries;

		try
		{
			foreach (var line in File.ReadAllLines(vdfPath))
			{
				// Lines look like:   "path"    "D:\\Games\\Steam"
				if (!line.TrimStart().StartsWith("\"path\"")) continue;

				var path = line.Split('"')[3].Replace(@"\\", @"\");
				Debug.WriteLine($"[FindSteamLibraries] vdf-ben talalt path: {path} | letezik: {Directory.Exists(path)}");
				if (Directory.Exists(path))
					libraries.Add(path);
			}
		}
		catch (Exception ex)
		{
			Debug.WriteLine($"[FindSteamLibraries] HIBA a vdf feldolgozasakor: {ex}");
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
				var doc = JsonDocument.Parse(json);
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
					var fullPath = Path.Combine(installPath, launchExe);
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
					Source = "Epic"
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
					Source = "Ubisoft"
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
						Source = "EA"
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
					Source = "GOG"
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
					Source = "BattleNet"
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

	private IEnumerable<string> EnumerateSafe(string root)
	{
		if (ShouldExclude(root)) yield break;

		// "*.exe" is passed to the OS-level API — non-exe files are never touched
		IEnumerable<string> files = Enumerable.Empty<string>();
		try { files = Directory.EnumerateFiles(root, "*.exe"); }
		catch (UnauthorizedAccessException) { }
		catch (IOException) { }

		foreach (var f in files)
		{
			if (IsLikelyNonGameExecutable(f)) continue;
			yield return f;
		}

		IEnumerable<string> dirs = Enumerable.Empty<string>();
		try { dirs = Directory.EnumerateDirectories(root); }
		catch (UnauthorizedAccessException) { }
		catch (IOException) { }

		foreach (var dir in dirs)
			foreach (var f in EnumerateSafe(dir))
				yield return f;
	}

	private static bool ShouldExclude(string path)
	{
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

				// New source is more trustworthy — upgrade the name
				// The object reference in seen and target point to the same instance
				// so mutating existing.GameName updates both automatically
				if (newPriority < existingPriority)
				{
					existing.GameName = item.GameName;
					existing.Source = item.Source;
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