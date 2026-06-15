using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using LaunchPad.Model;
using LaunchPad.Services;

namespace LaunchPad.ViewModel
{
	internal class HomeViewModel
	{
		private readonly GameService _gameService;
		public ObservableCollection<Game> Favourites { get; }
		public HomeViewModel(GameService gameService)
		{
			_gameService = gameService;
			Favourites = new ObservableCollection<Game>(_gameService.Games.Where(g => g.IsFavourite));

			foreach (var game in _gameService.Games)
			{
				game.PropertyChanged += OnGamePropertyChanged;
			}

			_gameService.Games.CollectionChanged += OnCollectionChanged;
		}

		private void OnGamePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
		{
			if (e.PropertyName != nameof(Game.IsFavourite)) return;
			if (sender is not Game game) return;
			if (game.IsFavourite && !Favourites.Contains(game)) Favourites.Add(game);
			else if (!game.IsFavourite && Favourites.Contains(game)) Favourites.Remove(game);
		}

		private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.NewItems != null)
			{
				foreach (Game game in e.NewItems)
				{
					game.PropertyChanged += OnGamePropertyChanged;
					if (game.IsFavourite) Favourites.Add(game);
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
