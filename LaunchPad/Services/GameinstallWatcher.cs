using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	public class GameInstallWatcher : IDisposable
	{
		private readonly GameService _gameService;
		private readonly SettingsService _settingsService;
		private readonly List<FileSystemWatcher> _watchers = new();
		private readonly Dictionary<string, FileSystemWatcher> _exeWatchersByPath =
			new(StringComparer.OrdinalIgnoreCase);

		// Gyors (platform) scannerek debounce-a
		private readonly object _debounceLock = new();
		private Timer? _debounceTimer;

		// Custom mappák debounce-a: ezeknél a MÉLYSZKENNELÉST kell lefuttatni az érintett mappán,
		// a gyors scannerek (Steam/Epic/...) a custom mappákat nem látják.
		private readonly object _customLock = new();
		private readonly HashSet<string> _pendingCustomFolders = new(StringComparer.OrdinalIgnoreCase);
		private Timer? _customDebounceTimer;

		private NotifyCollectionChangedEventHandler? _collectionChangedHandler;
		private bool _disposed;

		private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(5);

		public GameInstallWatcher(GameService gameService, SettingsService settingsService)
		{
			_gameService = gameService;
			_settingsService = settingsService;
		}

		/// <summary>A UI szálról hívandó (az App.OnStartup-ból).</summary>
		public void Start()
		{
			foreach (var steamLibrary in GameScanner.FindSteamLibraries())
			{
				var commonPath = Path.Combine(steamLibrary, "steamapps", "common");
				WatchForNewFolders(commonPath);
			}

			WatchForNewFiles(
				@"C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests",
				"*.item");

			WatchForNewFolders(@"C:\Program Files (x86)\Ubisoft\Ubisoft Game Launcher\games");

			WatchForNewFolders(@"C:\Program Files\EA Games");
			WatchForNewFolders(@"C:\Program Files (x86)\Origin Games");

			WatchForNewFolders(@"C:\Program Files (x86)\GOG Galaxy\Games");

			WatchForNewFiles(
				Path.Combine(
					Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
					@"Microsoft\Windows\Start Menu\Programs"),
				"*.lnk",
				includeSubdirectories: true);

			WatchForNewFiles(
				@"C:\ProgramData\Microsoft\Windows\Start Menu\Programs",
				"*.lnk",
				includeSubdirectories: true);

			foreach (var customFolder in _settingsService.Current.CustomGameFolders.ToList())
			{
				WatchCustomFolder(customFolder);
			}

			// Minden mar ismert jatek exejere kulon, azonnali torles-figyelo -
			// launchertol fuggetlenul eszreveszi, ha egy konkret exe eltunik,
			// meg akkor is, ha a korulotte levo mappa nem valtozik (pl.
			// Battle.net-es jatekok, amiket a fenti mappa-watcherek nem
			// figyelnek).
			foreach (var game in _gameService.Games.ToList())
			{
				WatchGameExecutable(game);
				game.PropertyChanged += OnGamePropertyChanged;
			}

			_collectionChangedHandler = OnGamesCollectionChanged;
			_gameService.Games.CollectionChanged += _collectionChangedHandler;
		}

		// A később hozzáadott játékok is kapjanak exe-watchert ÉS PropertyChanged feliratkozást
		// (különben egy új játéknál a "Change exe path" nem állítaná át a figyelt fájlt).
		private void OnGamesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.NewItems != null)
			{
				foreach (Game game in e.NewItems)
				{
					game.PropertyChanged += OnGamePropertyChanged;
					WatchGameExecutable(game);
				}
			}

			if (e.OldItems != null)
			{
				foreach (Game game in e.OldItems)
					game.PropertyChanged -= OnGamePropertyChanged;

				RemoveUnusedExeWatchers();
			}
		}

		/// <summary>
		/// Custom mappa figyelése. Új alkönyvtár / exe megjelenésekor az ÉRINTETT mappán
		/// mélyszken fut (a gyors scannerek a custom mappákat nem ismerik).
		/// </summary>
		public void WatchCustomFolder(string path)
		{
			if (_disposed || string.IsNullOrWhiteSpace(path)) return;

			var watcher = TryCreateWatcher(
				() => new FileSystemWatcher(path)
				{
					NotifyFilter = NotifyFilters.DirectoryName | NotifyFilters.FileName,
					IncludeSubdirectories = false,
				},
				path);

			if (watcher == null) return;

			watcher.Created += (_, _) => OnCustomFolderChanged(path);
			watcher.Deleted += (_, _) => OnCustomFolderChanged(path);
			watcher.Renamed += (_, _) => OnCustomFolderChanged(path);
			Register(watcher);
		}

		private void WatchForNewFolders(string path)
		{
			if (!Directory.Exists(path)) return;

			var watcher = TryCreateWatcher(
				() => new FileSystemWatcher(path)
				{
					NotifyFilter = NotifyFilters.DirectoryName,
					IncludeSubdirectories = false,
				},
				path);

			if (watcher == null) return;

			watcher.Created += OnChangeDetected;
			watcher.Deleted += OnChangeDetected;
			Register(watcher);
		}

		private void WatchForNewFiles(string path, string filter, bool includeSubdirectories = false)
		{
			if (!Directory.Exists(path)) return;

			var watcher = TryCreateWatcher(
				() => new FileSystemWatcher(path, filter)
				{
					NotifyFilter = NotifyFilters.FileName,
					IncludeSubdirectories = includeSubdirectories,
				},
				path);

			if (watcher == null) return;

			watcher.Created += OnChangeDetected;
			watcher.Deleted += OnChangeDetected;
			Register(watcher);
		}

		// Az EnableRaisingEvents dobhat (hozzáférés megtagadva, a mappa közben eltűnt, lecsatolt
		// meghajtó) - egy ilyen figyelő hibája ne vigye el az egész indítást.
		private static FileSystemWatcher? TryCreateWatcher(Func<FileSystemWatcher> factory, string path)
		{
			FileSystemWatcher? watcher = null;
			try
			{
				watcher = factory();
				watcher.EnableRaisingEvents = true;
				return watcher;
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[GameInstallWatcher] Nem figyelhető: {path} | {ex.Message}");
				watcher?.Dispose();
				return null;
			}
		}

		private void Register(FileSystemWatcher watcher)
		{
			_watchers.Add(watcher);
		}

		// -------------------------------------------------------------------
		// GYORS SCANNEREK (Steam / Epic / Start Menu / ...)
		// -------------------------------------------------------------------

		private void OnChangeDetected(object sender, FileSystemEventArgs e)
		{
			lock (_debounceLock)
			{
				if (_disposed) return;

				_debounceTimer?.Dispose();
				_debounceTimer = new Timer(
					callback: async _ => await RunScanSafelyAsync(),
					state: null,
					dueTime: DebounceDelay,
					period: Timeout.InfiniteTimeSpan);
			}
		}

		private async Task RunScanSafelyAsync()
		{
			try
			{
				if (_disposed) return;
				await _gameService.ScanAndMergeAsync();
				_gameService.MarkMissingGamesAsDeleted();
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[GameInstallWatcher] Scan hiba: {ex.Message}");
			}
		}

		// -------------------------------------------------------------------
		// CUSTOM MAPPÁK
		// -------------------------------------------------------------------

		private void OnCustomFolderChanged(string folder)
		{
			lock (_customLock)
			{
				if (_disposed) return;

				_pendingCustomFolders.Add(folder);

				_customDebounceTimer?.Dispose();
				_customDebounceTimer = new Timer(
					callback: async _ => await RunCustomScanSafelyAsync(),
					state: null,
					dueTime: DebounceDelay,
					period: Timeout.InfiniteTimeSpan);
			}
		}

		private async Task RunCustomScanSafelyAsync()
		{
			List<string> folders;
			lock (_customLock)
			{
				folders = _pendingCustomFolders.ToList();
				_pendingCustomFolders.Clear();
			}

			if (folders.Count == 0 || _disposed) return;

			try
			{
				await _gameService.DeepScanAndMergeAsync(folders);
				_gameService.MarkMissingGamesAsDeleted();

				// A settings listát/szótárat a UI szál módosítja máshol is -> oda marshal-oljuk.
				var dispatcher = System.Windows.Application.Current?.Dispatcher;
				dispatcher?.Invoke(() =>
				{
					foreach (var folder in folders)
						_settingsService.Current.CustomFolderLastScanUtc[folder] = DateTime.UtcNow;
					_settingsService.Save();
				});
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[GameInstallWatcher] Custom scan hiba: {ex.Message}");
			}
		}

		// -------------------------------------------------------------------
		// PER-EXE TORLES FIGYELES
		// -------------------------------------------------------------------

		private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			// Ha a jatek Source-a megvaltozik (pl. "Change exe path"), az uj
			// path-ra is fel kell venni egy watchert, a regit pedig le kell szedni.
			if (e.PropertyName != nameof(Game.Source)) return;
			if (sender is Game game)
			{
				WatchGameExecutable(game);
				RemoveUnusedExeWatchers();
			}
		}

		private void WatchGameExecutable(Game game)
		{
			// A watcher a MOST figyelt útvonalat jegyzi meg (nem a game.Source-ot olvassa az eseménykor):
			// ha közben a Source megváltozott, a régi fájl törlése nem jelölheti töröltnek az új exe-t.
			var watchedPath = game.Source;

			if (string.IsNullOrWhiteSpace(watchedPath)) return;
			if (_exeWatchersByPath.ContainsKey(watchedPath)) return; // ezt a fajlt mar figyeljuk

			var folder = Path.GetDirectoryName(watchedPath);
			var fileName = Path.GetFileName(watchedPath);
			if (string.IsNullOrEmpty(folder) || string.IsNullOrEmpty(fileName) || !Directory.Exists(folder)) return;

			var watcher = TryCreateWatcher(
				() => new FileSystemWatcher(folder, fileName)
				{
					NotifyFilter = NotifyFilters.FileName,
					IncludeSubdirectories = false,
				},
				watchedPath);

			if (watcher == null) return;

			// Az eseménykor mindig a fájl TÉNYLEGES állapotát nézzük (egy átnevezés, ami
			// ide érkezik, pl. "game.exe.tmp" -> "game.exe", nem törlés).
			watcher.Deleted += (_, _) => SyncDeletedStateOnUi(watchedPath);
			watcher.Renamed += (_, _) => SyncDeletedStateOnUi(watchedPath);
			watcher.Created += (_, _) => SyncDeletedStateOnUi(watchedPath);

			_exeWatchersByPath[watchedPath] = watcher;
			_watchers.Add(watcher);
		}

		// Azokat a fájlfigyelőket, amelyik útvonalat már egyetlen játék sem használja
		// (pl. "Change exe path" után a régi exe, vagy összevont duplikátum), leszedjük.
		private void RemoveUnusedExeWatchers()
		{
			var inUse = new HashSet<string>(
				_gameService.Games.Select(g => g.Source).Where(s => !string.IsNullOrWhiteSpace(s)),
				StringComparer.OrdinalIgnoreCase);

			foreach (var path in _exeWatchersByPath.Keys.Where(k => !inUse.Contains(k)).ToList())
			{
				var watcher = _exeWatchersByPath[path];
				_exeWatchersByPath.Remove(path);
				_watchers.Remove(watcher);

				try
				{
					watcher.EnableRaisingEvents = false;
					watcher.Dispose();
				}
				catch (Exception ex)
				{
					Debug.WriteLine($"[GameInstallWatcher] Watcher leszedése: {ex.Message}");
				}
			}
		}

		// A FileSystemWatcher esemenyei hatterszalon futnak, a Games gyujtemeny es a Game
		// tulajdonsagai viszont (a Library/Home nezetek miatt) csak a UI szalrol modosithatok.
		private void SyncDeletedStateOnUi(string path)
		{
			bool isDeleted;
			try { isDeleted = !File.Exists(path); }
			catch { return; }

			var dispatcher = System.Windows.Application.Current?.Dispatcher;
			if (dispatcher == null) return;

			dispatcher.BeginInvoke(new Action(() =>
			{
				if (_disposed) return;

				var game = _gameService.Games.FirstOrDefault(g =>
					string.Equals(g.Source, path, StringComparison.OrdinalIgnoreCase));

				if (game != null && game.IsDeleted != isDeleted)
					game.IsDeleted = isDeleted;
			}));
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			if (_collectionChangedHandler != null)
				_gameService.Games.CollectionChanged -= _collectionChangedHandler;

			foreach (var game in _gameService.Games)
				game.PropertyChanged -= OnGamePropertyChanged;

			foreach (var watcher in _watchers)
			{
				try
				{
					watcher.EnableRaisingEvents = false;
					watcher.Dispose();
				}
				catch { }
			}
			_watchers.Clear();
			_exeWatchersByPath.Clear();

			lock (_debounceLock)
			{
				_debounceTimer?.Dispose();
				_debounceTimer = null;
			}

			lock (_customLock)
			{
				_customDebounceTimer?.Dispose();
				_customDebounceTimer = null;
				_pendingCustomFolders.Clear();
			}
		}
	}
}
