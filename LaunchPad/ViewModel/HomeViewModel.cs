using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using LaunchPad.Model;
using LaunchPad.Services;

namespace LaunchPad.ViewModel
{
	internal partial class HomeViewModel : IDisposable
	{
		private readonly GameService _gameService;
		private readonly GameLauncher _launcher;
		private bool _disposed;

		public ObservableCollection<Game> Favourites { get; }

		public int TotalGamesCount => _gameService.TotalGamesCount;

		public HomeViewModel(GameService gameService, GameLauncher launcher)
		{
			_gameService = gameService;
			_launcher = launcher;
			Favourites = new ObservableCollection<Game>(_gameService.Games.Where(IsShownAsFavourite));

			foreach (var game in _gameService.Games)
			{
				game.PropertyChanged += OnGamePropertyChanged;
			}

			_gameService.Games.CollectionChanged += OnCollectionChanged;
		}

		/// <summary>
		/// A kizárt ("Excluded") játék nem jelenik meg sehol, a törölt (hiányzó exe) játékot pedig
		/// nem lehet elindítani - egyik sem való a kedvencek közé a főoldalon.
		/// </summary>
		private static bool IsShownAsFavourite(Game game) =>
			game.IsFavourite && !game.IsExcluded && !game.IsDeleted;

		/// <summary>
		/// A nézet minden megnyitáskor új VM-et kap; a Dispose nélkül a régi VM-ek örökre
		/// életben maradnának és minden játékváltozásra dolgoznának (memóriaszivárgás).
		/// </summary>
		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			_gameService.Games.CollectionChanged -= OnCollectionChanged;
			foreach (var game in _gameService.Games)
				game.PropertyChanged -= OnGamePropertyChanged;
		}

		[RelayCommand]
		private void Play(Game? game)
		{
			if (game == null) return;
			_launcher.LaunchWithFeedback(game);
		}

		private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName != nameof(Game.IsFavourite)
			 && e.PropertyName != nameof(Game.IsDeleted)
			 && e.PropertyName != nameof(Game.IsExcluded)) return;
			if (sender is not Game game) return;

			bool shown = IsShownAsFavourite(game);
			if (shown && !Favourites.Contains(game)) Favourites.Add(game);
			else if (!shown && Favourites.Contains(game)) Favourites.Remove(game);
		}

		private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.NewItems != null)
			{
				foreach (Game game in e.NewItems)
				{
					game.PropertyChanged += OnGamePropertyChanged;
					if (IsShownAsFavourite(game)) Favourites.Add(game);
				}
			}
			if (e.OldItems != null)
				foreach (Game game in e.OldItems)
				{
					game.PropertyChanged -= OnGamePropertyChanged;
					Favourites.Remove(game);
				}
		}
	}
}
