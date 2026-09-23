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
        private readonly SettingsService _settingsService;
        private readonly System.Timers.Timer _safetyTimer;
        public ObservableCollection<Game> Games { get; } = new();

        public int TotalGamesCount => Games.Count;

        public GameService(GameStorage storage, GameScanner scanner, SettingsService settingsService)
        {
            _storage = storage;
            _scanner = scanner;
            _settingsService = settingsService;

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
            var excludedPaths = _settingsService.Current.ExcludedGamePaths;

            foreach (var result in scanResults)
            {
                // A felhasznalo altal kifejezetten kizart (vagy "Change exe path"-
                // szal lecserelt) path-okat egyetlen scanner sem hozhatja vissza.
                if (excludedPaths.Any(p => string.Equals(p, result.ExePath, StringComparison.OrdinalIgnoreCase)))
                    continue;

                // Kizart jatekokat nev alapjan sem "elesztunk fel" automatikusan -
                // ha ugyanaz a nev egy UJ path-on bukkan fel, az egy uj bejegyzes
                // lesz, nem irja felul a mar kizart regi rekordot.
                var existing = Games.FirstOrDefault(g =>
                    !g.IsExcluded &&
                    string.Equals(g.Name, result.GameName, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    if (!string.Equals(existing.Source, result.ExePath, StringComparison.OrdinalIgnoreCase))
                        existing.Source = result.ExePath;
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

            foreach (var game in Games.Where(g => !g.IsDeleted && !g.IsExcluded))
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

        /// <summary>
        /// A jatekot "kizartra" allitja: mostantol nem jelenik meg a Libraryban,
        /// es a jelenlegi Source path-ja felkerul a kizart utak listajara, hogy
        /// egyetlen jovobeli scan se hozza vissza/hozza letre ujra ugyanezen
        /// az uton. Maga a rekord (statisztikak, session-ok) megmarad a
        /// Games.json-ban.
        /// </summary>
        public void ExcludeGame(Game game)
        {
            AddExcludedPath(game.Source);
            game.IsExcluded = true;

            _settingsService.Save();
            Save();
        }
        public void IncludeGame(Game game)
        {
            var excluded = _settingsService.Current.ExcludedGamePaths;
            excluded.RemoveAll(p => string.Equals(p, game.Source, StringComparison.OrdinalIgnoreCase));

            game.IsExcluded = false;

            _settingsService.Save();
            Save();
        }

        /// <summary>
        /// Lecsereli a jatek inditando exe-jet egy uj, felhasznalo altal
        /// kivalasztott path-ra. A REGI path felkerul a kizart utak listajara,
        /// hogy egy kesobbi scan (ami meg mindig megtalalhatja a regi exe-t,
        /// pl. ha az meg mindig a lemezen van) ne irja felul vissza a Source-ot.
        /// Az uj path kozvetlenul kerul beallitasra, nem a scan-merge-en
        /// keresztul, igy a regi es az uj path sosem utkozhet.
        /// </summary>
        public void ChangeGameExecutable(Game game, string newExePath)
        {
            var oldPath = game.Source;

            if (!string.Equals(oldPath, newExePath, StringComparison.OrdinalIgnoreCase))
                AddExcludedPath(oldPath);

            game.Source = newExePath;

            _settingsService.Save();
            Save();
        }

        private void AddExcludedPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            var excluded = _settingsService.Current.ExcludedGamePaths;
            bool alreadyThere = excluded.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

            if (!alreadyThere) excluded.Add(path);
        }


        public void Save()
        {
            _storage.SaveGames(Games.ToList());
        }
    }
}