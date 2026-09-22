using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LaunchPad.Services
{
	public class GameInstallWatcher : IDisposable
	{
		private readonly GameService _gameService;
		private readonly SettingsService _settingsService;
		private readonly List<FileSystemWatcher> _watchers = new();
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
		public void Dispose()
		{
			foreach (var watcher in _watchers)
			{
				watcher.EnableRaisingEvents = false;
				watcher.Dispose();
			}
			_watchers.Clear();

			lock (_debounceLock)
			{
				_debounceTimer?.Dispose();
				_debounceTimer = null;
			}
		}
	}
}