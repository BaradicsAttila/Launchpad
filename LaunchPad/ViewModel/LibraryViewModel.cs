using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LaunchPad.Model;
using LaunchPad.Services;

namespace LaunchPad.ViewModel
{
	public partial class LibraryViewModel : ObservableObject
	{
		private readonly GameService _gameService;

		public ObservableCollection<Game> Games { get; }

		public LibraryViewModel(GameService gameService)
		{
			_gameService = gameService;

			Games = new ObservableCollection<Game>(_gameService.Games.Where(g => !g.IsDeleted));

			foreach (var game in _gameService.Games)
				game.PropertyChanged += OnGamePropertyChanged;

			_gameService.Games.CollectionChanged += OnCollectionChanged;
		}

		[RelayCommand]
		private void ToggleFavourite(Game? game)
		{
			if (game == null) return;
			game.IsFavourite = !game.IsFavourite;
		}

		private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName != nameof(Game.IsDeleted)) return;
			if (sender is not Game game) return;

			if (game.IsDeleted) Games.Remove(game);
			else if (!Games.Contains(game)) Games.Add(game);
		}

		private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.NewItems != null)
			{
				foreach (Game game in e.NewItems)
				{
					game.PropertyChanged += OnGamePropertyChanged;
					if (!game.IsDeleted) Games.Add(game);
				}
			}

			if (e.OldItems != null)
			{
				foreach (Game game in e.OldItems)
				{
					game.PropertyChanged -= OnGamePropertyChanged;
					Games.Remove(game);
				}
			}
		}
	}
}