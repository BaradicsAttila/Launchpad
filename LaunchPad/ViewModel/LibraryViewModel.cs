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

            Games = new ObservableCollection<Game>(_gameService.Games.Where(IsVisible));

            foreach (var game in _gameService.Games)
                game.PropertyChanged += OnGamePropertyChanged;

            _gameService.Games.CollectionChanged += OnCollectionChanged;
        }

        private static bool IsVisible(Game game) => !game.IsDeleted && !game.IsExcluded;

        [RelayCommand]
        private void ToggleFavourite(Game? game)
        {
            if (game == null) return;
            game.IsFavourite = !game.IsFavourite;
        }

        /// <summary>
        /// A jatek path-jat felveszi a kizart utak koze, es elrejti a
        /// Libraryból - a Games.json-ban a rekord megmarad.
        /// </summary>
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

        /// <summary>
        /// Filepicker-rel uj exe-t valaszthat a jatekhoz - a regi path
        /// automatikusan kizart utnak szamit majd, hogy a kovetkezo scan
        /// ne allitsa vissza.
        /// </summary>
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
            if (e.PropertyName != nameof(Game.IsDeleted) && e.PropertyName != nameof(Game.IsExcluded)) return;
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