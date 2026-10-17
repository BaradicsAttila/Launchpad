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
		private const string EveryGameName = "Every game";
		private const string FavouritesName = "Favourites";
		private const string DeletedName = "Deleted";
		private const string ExcludedName = "Excluded";

		private readonly GameService _gameService;

		public ObservableCollection<Game> Games { get; }

		// SHOW csoport: Every game / Favourites / Deleted / Excluded
		public FilterGroup ShowFilter { get; }

		// SOURCE csoport: Steam / Epic / ...
		public FilterGroup SourceFilter { get; }

		public LibraryViewModel(GameService gameService)
		{
			_gameService = gameService;

			// Alapból csak az "Every game" chip van kijelölve
			ShowFilter = new FilterGroup(
				new[] { EveryGameName, FavouritesName, DeletedName, ExcludedName },
				initiallySelected: new[] { EveryGameName });

			SourceFilter = new FilterGroup(
				new[] { "Steam", "Epic", "Ubisoft", "EA", "GOG", "Battle.net", "Other" });

			ShowFilter.Changed += (_, _) => RefreshGames();
			SourceFilter.Changed += (_, _) => RefreshGames();

			Games = new ObservableCollection<Game>(_gameService.Games.Where(IsVisible));

			foreach (var game in _gameService.Games)
				game.PropertyChanged += OnGamePropertyChanged;

			_gameService.Games.CollectionChanged += OnCollectionChanged;
		}

		private bool IsVisible(Game game)
		{
			// 1) Állapot szerinti szűrés.
			//    Ha egy chip sincs kijelölve (All), nincs szűrés: minden látszik.
			//    Különben a kijelölt kategóriák UNIÓJA látszik.
			bool isRegular = !game.IsDeleted && !game.IsExcluded;

			bool matchesState =
				!ShowFilter.HasSelection ||
				(isRegular && ShowFilter.IsSelected(EveryGameName)) ||
				(isRegular && game.IsFavourite && ShowFilter.IsSelected(FavouritesName)) ||
				(game.IsDeleted && ShowFilter.IsSelected(DeletedName)) ||
				(game.IsExcluded && ShowFilter.IsSelected(ExcludedName));

			if (!matchesState) return false;

			// 2) Source szűrés: ha egy sincs bejelölve, nincs szűrés
			var selected = SourceFilter.SelectedNames;
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