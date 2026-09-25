using System;
using System.Collections.Generic;
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
		private readonly Dictionary<string, FileSystemWatcher> _exeWatchersByPath = new();
		private readonly object _debounceLock = new();
		private Timer? _debounceTimer;

		private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(5);

		public GameInstallWatcher(GameService gameService, SettingsService settingsService)
		{
			_gameService = gameService;
			_settingsService = settingsService;
		}
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
			foreach (var customFolder in _settingsService.Current.CustomGameFolders)
			{
				WatchCustomFolder(customFolder);
			}

			// Minden mar ismert jatek exejere kulon, azonnali torles-figyelo -
			// launchertol fuggetlenul eszreveszi, ha egy konkret exe eltunik,
			// meg akkor is, ha a korulotte levo mappa nem valtozik (pl.
			// Battle.net-es jatekok, amiket a fenti mappa-watcherek nem
			// figyelnek).
			foreach (var game in _gameService.Games)
				WatchGameExecutable(game);

			_gameService.Games.CollectionChanged += (_, e) =>
			{
				if (e.NewItems != null)
					foreach (Game g in e.NewItems)
						WatchGameExecutable(g);
			};

			foreach (var game in _gameService.Games)
				game.PropertyChanged += OnGamePropertyChanged;
		}

		public void WatchCustomFolder(string path)
		{
			WatchForNewFolders(path);
		}

		private void WatchForNewFolders(string path)
		{
			if (!Directory.Exists(path)) return;

			var watcher = new FileSystemWatcher(path)
			{
				NotifyFilter = NotifyFilters.DirectoryName,
				IncludeSubdirectories = false,
				EnableRaisingEvents = true
			};
			watcher.Created += OnChangeDetected;
			watcher.Deleted += OnChangeDetected;
			_watchers.Add(watcher);
		}

		private void WatchForNewFiles(string path, string filter, bool includeSubdirectories = false)
		{
			if (!Directory.Exists(path)) return;

			var watcher = new FileSystemWatcher(path, filter)
			{
				NotifyFilter = NotifyFilters.FileName,
				IncludeSubdirectories = includeSubdirectories,
				EnableRaisingEvents = true
			};
			watcher.Created += OnChangeDetected;
			watcher.Deleted += OnChangeDetected;
			_watchers.Add(watcher);
		}

		private void OnChangeDetected(object sender, FileSystemEventArgs e)
		{
			lock (_debounceLock)
			{
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
				await _gameService.ScanAndMergeAsync();
				_gameService.MarkMissingGamesAsDeleted();
			}
			catch
			{
			}
		}

		// -------------------------------------------------------------------
		// PER-EXE TORLES FIGYELES
		// -------------------------------------------------------------------

		private void OnGamePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			// Ha a jatek Source-a megvaltozik (pl. "Change exe path"), az uj
			// path-ra is fel kell venni egy watchert.
			if (e.PropertyName != nameof(Game.Source)) return;
			if (sender is Game game) WatchGameExecutable(game);
		}

		private void WatchGameExecutable(Game game)
		{
			if (string.IsNullOrWhiteSpace(game.Source)) return;
			if (_exeWatchersByPath.ContainsKey(game.Source)) return; // ezt a fajlt mar figyeljuk

			var folder = Path.GetDirectoryName(game.Source);
			var fileName = Path.GetFileName(game.Source);
			if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;

			var watcher = new FileSystemWatcher(folder, fileName)
			{
				NotifyFilter = NotifyFilters.FileName,
				IncludeSubdirectories = false,
				EnableRaisingEvents = true
			};

			watcher.Deleted += (_, __) => MarkGameAsDeletedByPath(game.Source);
			watcher.Renamed += (_, __) => MarkGameAsDeletedByPath(game.Source);
			watcher.Created += (_, __) => MarkGameAsRestoredByPath(game.Source);

			_exeWatchersByPath[game.Source] = watcher;
			_watchers.Add(watcher);
		}

		private void MarkGameAsDeletedByPath(string path)
		{
			var game = _gameService.Games.FirstOrDefault(g =>
				string.Equals(g.Source, path, StringComparison.OrdinalIgnoreCase));

			if (game != null && !game.IsDeleted)
				game.IsDeleted = true;
		}

		// Ha ugyanoda (ugyanaz a mappa + fajlnev) kerul vissza az exe - pl. a
		// felhasznalo ujra letolti a torolt jatekot - azonnal, kulon scan
		// nelkul visszaallitjuk a lathato allapotot.
		private void MarkGameAsRestoredByPath(string path)
		{
			var game = _gameService.Games.FirstOrDefault(g =>
				string.Equals(g.Source, path, StringComparison.OrdinalIgnoreCase));

			if (game != null && game.IsDeleted)
				game.IsDeleted = false;
		}

		public void Dispose()
		{
			foreach (var watcher in _watchers)
			{
				watcher.EnableRaisingEvents = false;
				watcher.Dispose();
			}
			_watchers.Clear();
			_exeWatchersByPath.Clear();

			lock (_debounceLock)
			{
				_debounceTimer?.Dispose();
				_debounceTimer = null;
			}
		}
	}
}