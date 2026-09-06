using LaunchPad.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Linq;

namespace LaunchPad.Services
{
	public class GameService
	{
		private readonly GameStorage _storage;
		private readonly System.Timers.Timer _safetyTimer;
		public ObservableCollection<Game> Games { get; } = new();
		public GameService(GameStorage storage)
		{
			_storage = storage;
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
			foreach(var result in scanResults)
			{
				var existing = Games.FirstOrDefault(g=>string.Equals(g.Name, result.GameName, StringComparison.OrdinalIgnoreCase));
				if (existing != null)
				{
					if (!string.Equals(existing.Source, result.Source, StringComparison.OrdinalIgnoreCase)) existing.Source = result.Source;
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
		public void Save()
		{
			_storage.SaveGames(Games.ToList());
		}
	}
}