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

	// -------------------------------------------------------------------------
	// PUBLIC API
	// -------------------------------------------------------------------------

	/// <summary>
	/// Runs all instant scans (Start Menu, Registry, Steam, Epic, Ubisoft, EA, GOG, Battle.net).
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
			AddRange(results, seen, GetFromSteam());

			progress?.Report("Reading Epic Games manifests...");
			AddRange(results, seen, GetFromEpic());

			progress?.Report("Reading Start Menu shortcuts...");
			AddRange(results, seen, GetFromStartMenu());

			progress?.Report("Reading Registry...");
			AddRange(results, seen, GetFromRegistry());

			progress?.Report("Reading Ubisoft Connect...");
			AddRange(results, seen, GetFromUbisoft());

			progress?.Report("Reading EA App...");
			AddRange(results, seen, GetFromEA());

			progress?.Report("Reading GOG Galaxy...");
			AddRange(results, seen, GetFromGOG());

			progress?.Report("Reading Battle.net...");
			AddRange(results, seen, GetFromBattleNet());

			progress?.Report($"Done — found {results.Count} games.");
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
			if (!Directory.Exists(root)) continue;

			foreach (var lnk in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
			{
				try
				{
					var target = ResolveShortcut(lnk);

					if (string.IsNullOrEmpty(target)) continue;
					if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
					if (!File.Exists(target)) continue;
					if (new FileInfo(target).Length < 5_000_000) continue;

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
			if (root == null) continue;

			foreach (var subKeyName in root.GetSubKeyNames())
			{
				try
				{
					using var sub = root.OpenSubKey(subKeyName);
					if (sub == null) continue;

					var installLocation = sub.GetValue("InstallLocation")?.ToString();
					var displayName = sub.GetValue("DisplayName")?.ToString();

					if (string.IsNullOrEmpty(installLocation)) continue;
					if (!Directory.Exists(installLocation)) continue;

					// Only scan the top-level install folder (not recursive)
					// since InstallLocation points directly to the game folder
					var exe = Directory.EnumerateFiles(installLocation, "*.exe")
						.Select(f => new FileInfo(f))
						.Where(f => f.Length > 5_000_000)
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
				catch { }
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

		foreach (var library in FindSteamLibraries())
		{
			var steamAppsPath = Path.Combine(library, "steamapps");
			if (!Directory.Exists(steamAppsPath)) continue;

			foreach (var manifest in Directory.EnumerateFiles(steamAppsPath, "appmanifest_*.acf"))
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

					if (string.IsNullOrEmpty(installDir)) continue;

					var gamePath = Path.Combine(steamAppsPath, "common", installDir);
					if (!Directory.Exists(gamePath)) continue;

					var exe = Directory.EnumerateFiles(gamePath, "*.exe", SearchOption.AllDirectories)
						.Select(f => new FileInfo(f))
						.Where(f => f.Length > 5_000_000)
						.OrderByDescending(f => f.Length)
						.FirstOrDefault();

					if (exe == null) continue;

					results.Add(new GameScanResult
					{
						ExePath = exe.FullName,
						GameName = name ?? installDir,
						Source = "Steam"
					});
				}
				catch { }
			}
		}

		return results;
	}

	private static List<string> FindSteamLibraries()
	{
		var libraries = new List<string>();

		var defaultSteam = @"C:\Program Files (x86)\Steam";
		if (Directory.Exists(defaultSteam))
			libraries.Add(defaultSteam);

		// Steam stores all library paths in this file
		// This handles users who have games on D:\, E:\, etc.
		var vdfPath = Path.Combine(defaultSteam, @"steamapps\libraryfolders.vdf");
		if (!File.Exists(vdfPath)) return libraries;

		try
		{
			foreach (var line in File.ReadAllLines(vdfPath))
			{
				// Lines look like:   "path"    "D:\\Games\\Steam"
				if (!line.TrimStart().StartsWith("\"path\"")) continue;

				var path = line.Split('"')[3].Replace(@"\\", @"\");
				if (Directory.Exists(path))
					libraries.Add(path);
			}
		}
		catch { }

		return libraries;
	}

	// -------------------------------------------------------------------------
	// 4. EPIC GAMES MANIFESTS
	// -------------------------------------------------------------------------

	private static List<GameScanResult> GetFromEpic()
	{
		var results = new List<GameScanResult>();

		var manifestsPath = @"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests";
		if (!Directory.Exists(manifestsPath)) return results;

		foreach (var file in Directory.EnumerateFiles(manifestsPath, "*.item"))
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

				if (string.IsNullOrEmpty(installPath) || !Directory.Exists(installPath)) continue;

				string? exePath = null;

				if (!string.IsNullOrEmpty(launchExe))
				{
					var fullPath = Path.Combine(installPath, launchExe);
					if (File.Exists(fullPath))
						exePath = fullPath;
				}

				// Fallback if LaunchExecutable is missing or points to a wrapper
				exePath ??= Directory.EnumerateFiles(installPath, "*.exe", SearchOption.AllDirectories)
					.Select(f => new FileInfo(f))
					.Where(f => f.Length > 5_000_000)
					.OrderByDescending(f => f.Length)
					.FirstOrDefault()?.FullName;

				if (exePath == null) continue;

				results.Add(new GameScanResult
				{
					ExePath = exePath,
					GameName = displayName ?? Path.GetFileNameWithoutExtension(exePath),
					Source = "Epic"
				});
			}
			catch { }
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
		if (!Directory.Exists(root)) return results;

		foreach (var gameFolder in Directory.EnumerateDirectories(root))
		{
			try
			{
				var exe = Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.AllDirectories)
					.Select(f => new FileInfo(f))
					.Where(f => f.Length > 5_000_000)
					.OrderByDescending(f => f.Length)
					.FirstOrDefault();

				if (exe == null) continue;

				results.Add(new GameScanResult
				{
					ExePath = exe.FullName,
					GameName = Path.GetFileName(gameFolder),
					Source = "Ubisoft"
				});
			}
			catch { }
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

		foreach (var root in roots.Where(Directory.Exists))
		{
			foreach (var gameFolder in Directory.EnumerateDirectories(root))
			{
				try
				{
					var exe = Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.AllDirectories)
						.Select(f => new FileInfo(f))
						.Where(f => f.Length > 5_000_000)
						.OrderByDescending(f => f.Length)
						.FirstOrDefault();

					if (exe == null) continue;

					results.Add(new GameScanResult
					{
						ExePath = exe.FullName,
						GameName = Path.GetFileName(gameFolder),
						Source = "EA"
					});
				}
				catch { }
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
		if (!Directory.Exists(root)) return results;

		foreach (var gameFolder in Directory.EnumerateDirectories(root))
		{
			try
			{
				var exe = Directory.EnumerateFiles(gameFolder, "*.exe", SearchOption.AllDirectories)
					.Select(f => new FileInfo(f))
					.Where(f => f.Length > 5_000_000)
					.OrderByDescending(f => f.Length)
					.FirstOrDefault();

				if (exe == null) continue;

				results.Add(new GameScanResult
				{
					ExePath = exe.FullName,
					GameName = Path.GetFileName(gameFolder),
					Source = "GOG"
				});
			}
			catch { }
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

		foreach (var root in roots.Where(Directory.Exists))
		{
			try
			{
				var exe = Directory.EnumerateFiles(root, "*.exe", SearchOption.AllDirectories)
					.Select(f => new FileInfo(f))
					.Where(f => f.Length > 5_000_000)
					.OrderByDescending(f => f.Length)
					.FirstOrDefault();

				if (exe == null) continue;

				results.Add(new GameScanResult
				{
					ExePath = exe.FullName,
					GameName = Path.GetFileName(root),
					Source = "BattleNet"
				});
			}
			catch { }
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
			long size = 0;
			try { size = new FileInfo(f).Length; }
			catch { continue; }

			if (size > 5_000_000) yield return f;
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