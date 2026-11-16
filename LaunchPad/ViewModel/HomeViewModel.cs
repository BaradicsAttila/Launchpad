using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using CommunityToolkit.Mvvm.Input;
using LaunchPad.Model;
using LaunchPad.Services;

namespace LaunchPad.ViewModel
{
	internal partial class HomeViewModel
	{
		private readonly GameService _gameService;
		private readonly GameLauncher _launcher;
		public ObservableCollection<Game> Favourites { get; }

		public int TotalGamesCount => _gameService.TotalGamesCount;

		public HomeViewModel(GameService gameService, GameLauncher launcher)
		{
			_gameService = gameService;
			_launcher = launcher;
			Favourites = new ObservableCollection<Game>(_gameService.Games.Where(g => g.IsFavourite));

			foreach (var game in _gameService.Games)
			{
				game.PropertyChanged += OnGamePropertyChanged;
			}

			_gameService.Games.CollectionChanged += OnCollectionChanged;
		}

		[RelayCommand]
		private void Play(Game? game)
		{
			if (game == null) return;
			_launcher.LaunchWithFeedback(game);
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
