using LaunchPad.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Linq;
using System.Threading.Tasks;
using System.IO;

namespace LaunchPad.Services
{
	public class GameService
	{
		private readonly GameStorage _storage;
		private readonly GameScanner _scanner;
		private readonly System.Timers.Timer _safetyTimer;
		public ObservableCollection<Game> Games { get; } = new();

		public int TotalGamesCount => Games.Count;

		public GameService(GameStorage storage, GameScanner scanner)
		{
			_storage = storage;
			_scanner = scanner;

			var loaded = _storage.LoadGames();
			foreach (var game in loaded)
			{
				SubscribeToGame(game);
				Games.Add(game);
			}
			Games.CollectionChanged += OnCollectionChanged;
			_safetyTimer = new System.Timers.Timer(30000);
			_safetyTimer.Elapsed += (_, _) => FlushActiveSessions();
			_safetyTimer.AutoReset = true;
			_safetyTimer.Start();
		}
		public async Task ScanAndMergeAsync(IProgress<string>? progress = null)
		{
			var results = await _scanner.RunInstantScansAsync(progress);
			MergeWithScanResults(results);
			Save();
		}

		public async Task DeepScanAndMergeAsync(IEnumerable<string> folders, IProgress<string>? progress = null)
		{
			var results = await _scanner.ScanFoldersAsync(folders, progress);
			MergeWithScanResults(results);
			Save();
		}

		private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.NewItems != null)
			{
				foreach (Game game in e.NewItems)
				{
					SubscribeToGame(game);
				}
			}
			Save();
		}
		private void SubscribeToGame(Game game)
		{
			game.PropertyChanged += (_, _) => Save();
		}

		public void MergeWithScanResults(List<GameScanResult> scanResults)
		{
			foreach (var result in scanResults)
			{
				var existing = Games.FirstOrDefault(g => string.Equals(g.Name, result.GameName, StringComparison.OrdinalIgnoreCase));
				if (existing != null)
				{
					if (!string.Equals(existing.Source, result.ExePath, StringComparison.OrdinalIgnoreCase)) existing.Source = result.ExePath;
				}
				else
				{
					Games.Add(new Game(
					name: result.GameName,
					source: result.ExePath,
					isFavourite: false,
					isDeleted: false,
					sessions: null
					));
				}
			}
		}
		private void FlushActiveSessions()
		{
			var activeSessions = Games.Where(g => g.ActiveSession != null).ToList();
			if (!activeSessions.Any()) return;
			foreach (var game in activeSessions) game.FlushSession();
			Save();

		}

		public void MarkMissingGamesAsDeleted()
		{
			bool anyChanged = false;

			foreach (var game in Games.Where(g => !g.IsDeleted))
			{
				if (!File.Exists(game.Source))
				{
					game.IsDeleted = true;
					anyChanged = true;
				}
			}

			if (anyChanged) Save();
		}

		public List<string> GetChangedCustomFolders(
			List<string> customFolders,
			Dictionary<string, DateTime> lastScanTimes)
		{
			var changed = new List<string>();

			foreach (var folder in customFolders)
			{
				if (!Directory.Exists(folder)) continue;

				var lastWrite = Directory.GetLastWriteTimeUtc(folder);

				if (!lastScanTimes.TryGetValue(folder, out var lastKnown) || lastWrite > lastKnown)
				{
					changed.Add(folder);
				}
			}

			return changed;
		}

		public void Save()
		{
			_storage.SaveGames(Games.ToList());
		}
	}
}