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
		public ObservableCollection<FilterOption> Sources { get; } = new();

		[ObservableProperty] private bool showDeleted;
		[ObservableProperty] private bool showFavourites;
		[ObservableProperty] private bool showExcluded;

		partial void OnShowDeletedChanged(bool value) => RefreshGames();
		partial void OnShowFavouritesChanged(bool value) => RefreshGames();
		partial void OnShowExcludedChanged(bool value) => RefreshGames();

		public LibraryViewModel(GameService gameService)
		{
			_gameService = gameService;   // <-- javítva

			foreach (var name in new[] { "Steam", "Epic", "Ubisoft", "EA", "GOG", "Battle.net", "Other" })
			{
				var option = new FilterOption(name);
				option.PropertyChanged += (_, _) => RefreshGames();
				Sources.Add(option);
			}

			Games = new ObservableCollection<Game>(_gameService.Games.Where(IsVisible));

			foreach (var game in _gameService.Games)
				game.PropertyChanged += OnGamePropertyChanged;

			_gameService.Games.CollectionChanged += OnCollectionChanged;
		}

		private bool IsVisible(Game game)
		{
			if (game.IsDeleted && !ShowDeleted) return false;
			if (game.IsExcluded && !ShowExcluded) return false;
			if (ShowFavourites && !game.IsFavourite) return false;

			var selected = Sources.Where(s => s.IsChecked).Select(s => s.Name).ToList();
			if (selected.Count > 0 && !selected.Contains(game.Platform)) return false;

			return true;
		}

		private void RefreshGames()
		{
			Games.Clear();
			foreach (var game in _gameService.Games.Where(IsVisible))
				Games.Add(game);
		}

		[RelayCommand]
		private void ToggleFavourite(Game? game)
		{
			if (game == null) return;
			game.IsFavourite = !game.IsFavourite;
		}

		[RelayCommand]
		private void ExcludeGame(Game? game)
		{
			if (game == null) return;
			_gameService.ExcludeGame(game);
		}

		[RelayCommand]
		private void IncludeGame(Game? game)
		{
			if (game == null) return;
			_gameService.IncludeGame(game);
		}

		[RelayCommand]
		private void ChangeExePath(Game? game)
		{
			if (game == null) return;

			var dialog = new Microsoft.Win32.OpenFileDialog
			{
				Title = "Select the executable to launch for this game",
				Filter = "Executable files (*.exe)|*.exe",
				CheckFileExists = true
			};

			if (dialog.ShowDialog() != true) return;

			_gameService.ChangeGameExecutable(game, dialog.FileName);
		}

		private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName != nameof(Game.IsDeleted)
			 && e.PropertyName != nameof(Game.IsExcluded)
			 && e.PropertyName != nameof(Game.IsFavourite)
			 && e.PropertyName != nameof(Game.Source)) return;
			if (sender is not Game game) return;

			if (!IsVisible(game)) Games.Remove(game);
			else if (!Games.Contains(game)) Games.Add(game);
		}

		private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.NewItems != null)
			{
				foreach (Game game in e.NewItems)
				{
					game.PropertyChanged += OnGamePropertyChanged;
					if (IsVisible(game)) Games.Add(game);
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