using LaunchPad.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;

namespace LaunchPad.Services
{
	internal class GameService
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
			_safetyTimer.Elapsed += (_, _) => FlushActiveSession();
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

		private void MergeWithScanResoults(List<GameScanner.Sc>)

	}
}