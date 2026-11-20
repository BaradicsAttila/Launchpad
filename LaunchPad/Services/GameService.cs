
using LaunchPad.Helpers;
using LaunchPad.Model;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace LaunchPad.Services
{
	public class GameService : IDisposable
	{
		// Csak ezek a tulajdonságok kerülnek a Games.json-ba. A származtatott értékek
		// (IsRunning, TotalPlaytimeSeconds, ...) változása NEM triggerel mentést.
		private static readonly HashSet<string> PersistedProperties = new()
		{
			nameof(Game.Source),
			nameof(Game.IsFavourite),
			nameof(Game.IsDeleted),
			nameof(Game.IsExcluded),
			nameof(Game.InstallRoot),
		};

		private readonly GameStorage _storage;
		private readonly GameScanner _scanner;
		private readonly SettingsService _settingsService;
		private readonly Dispatcher _dispatcher;

		// Törlés/visszatöltés észlelése. DispatcherTimer -> a tick a UI szálon fut,
		// a lassú File.Exists ellenőrzések viszont háttérszálra mennek.
		private readonly DispatcherTimer _availabilityTimer;

		// Debounce-olt mentés: sok gyors változásból egyetlen írás lesz.
		private readonly DispatcherTimer _saveTimer;

		private int _availabilityCheckRunning;
		private bool _disposed;

		public ObservableCollection<Game> Games { get; } = new();

		public int TotalGamesCount => Games.Count;

		public GameService(GameStorage storage, GameScanner scanner, SettingsService settingsService)
		{
			_storage = storage;
			_scanner = scanner;
			_settingsService = settingsService;
			_dispatcher = Application.Current.Dispatcher;

			var loaded = _storage.LoadGames();
			foreach (var game in loaded)
			{
				SubscribeToGame(game);
				Games.Add(game);
			}
			Games.CollectionChanged += OnCollectionChanged;

			_saveTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
			{
				Interval = TimeSpan.FromSeconds(1)
			};
			_saveTimer.Tick += (_, _) =>
			{
				_saveTimer.Stop();
				Save();
			};

			_availabilityTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
			{
				Interval = TimeSpan.FromSeconds(3)
			};
			_availabilityTimer.Tick += async (_, _) => await RunAvailabilityCheckAsync();
			_availabilityTimer.Start();
		}

		// -------------------------------------------------------------------
		// SZÁLKEZELÉS SEGÉDEK
		// -------------------------------------------------------------------

		private void OnUi(Action action)
		{
			if (_dispatcher.CheckAccess()) action();
			else _dispatcher.Invoke(action);
		}

		private T OnUiResult<T>(Func<T> func)
		{
			return _dispatcher.CheckAccess() ? func() : _dispatcher.Invoke(func);
		}

		// -------------------------------------------------------------------
		// SCAN
		// -------------------------------------------------------------------

		public async Task ScanAndMergeAsync(IProgress<string>? progress = null)
		{
			var results = await _scanner.RunInstantScansAsync(progress).ConfigureAwait(false);

			// A Games gyűjteményt csak a UI szál módosíthatja - a GameInstallWatcher
			// timere háttérszálról hívja ezt a metódust.
			await _dispatcher.InvokeAsync(() =>
			{
				MergeWithScanResults(results);
				Save();
			});
		}

		public async Task DeepScanAndMergeAsync(IEnumerable<string> folders, IProgress<string>? progress = null)
		{
			var results = await _scanner.ScanFoldersAsync(folders, progress).ConfigureAwait(false);

			await _dispatcher.InvokeAsync(() =>
			{
				MergeWithScanResults(results);
				Save();
			});
		}

		/// <summary>Csak a UI szálról hívható.</summary>
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
				// ELŐSZÖR az exe útvonala alapján keresünk: ugyanarra az exe-re (pl. Start Menü
				// "XAMPP Control Panel" és mélyszken "xampp") sosem jöhet létre két bejegyzés.
				// Csak ha ilyen nincs, jön a név szerinti egyeztetés.
				var existing =
					Games.FirstOrDefault(g =>
						!g.IsExcluded &&
						string.Equals(g.Source, result.ExePath, StringComparison.OrdinalIgnoreCase))
					?? Games.FirstOrDefault(g =>
						!g.IsExcluded &&
						string.Equals(g.Name, result.GameName, StringComparison.OrdinalIgnoreCase));

				if (existing != null)
				{
					if (!string.Equals(existing.Source, result.ExePath, StringComparison.OrdinalIgnoreCase))
						existing.Source = result.ExePath;

					// A Source váltása null-ra állíthatta az InstallRoot-ot, ezért UTÁNA töltjük.
					// A régi rekordok is így kapják meg az InstallRoot-ot az első indításkor.
					if (!string.IsNullOrEmpty(result.InstallRoot) &&
						!string.Equals(existing.InstallRoot, result.InstallRoot, StringComparison.OrdinalIgnoreCase))
						existing.InstallRoot = result.InstallRoot;

					// A jatekot korabban torolve jeloltuk (a fajl eltunt), de a
					// scanner most ujra megtalalta - tehat visszatoltottek.
					if (existing.IsDeleted)
						existing.IsDeleted = false;
				}
				else
				{
					var game = new Game(
						name: result.GameName,
						source: result.ExePath,
						isFavourite: false,
						isDeleted: false,
						sessions: null);

					game.InstallRoot = result.InstallRoot;
					Games.Add(game);
				}
			}
		}

		// -------------------------------------------------------------------
		// DUPLIKÁTUMOK ÖSSZEVONÁSA
		// -------------------------------------------------------------------

		/// <summary>
		/// Az ugyanarra az exe-re mutató bejegyzéseket egybe olvasztja (a régi mentésekben
		/// maradt duplikátumokra). A sessionök egyesülnek, a kedvenc jelölés és az InstallRoot megmarad.
		/// A GameProcessMonitor.Start() ELŐTT hívandó, a UI szálról. Visszaadja a megszüntetett bejegyzések számát.
		/// </summary>
		public int RemoveDuplicateGames()
		{
			if (!_dispatcher.CheckAccess())
				return OnUiResult(RemoveDuplicateGames);

			var groups = Games
				.Where(g => !g.IsExcluded && !string.IsNullOrWhiteSpace(g.Source))
				.GroupBy(g => PathHelper.NormalizeFile(g.Source) ?? g.Source, StringComparer.OrdinalIgnoreCase)
				.Where(group => group.Count() > 1)
				.ToList();

			int removed = 0;

			foreach (var group in groups)
			{
				var list = group.ToList();

				// Kedvenc / több session nyer; egyenlőségnél a listában korábbi (a rendezés stabil).
				var keeper = list
					.OrderByDescending(g => g.IsFavourite)
					.ThenByDescending(g => g.Sessions.Count)
					.First();

				foreach (var other in list.Where(g => !ReferenceEquals(g, keeper)))
				{
					MergeInto(keeper, other);
					Games.Remove(other);
					removed++;
				}
			}

			if (removed > 0) Save();
			return removed;
		}

		private static void MergeInto(Game keeper, Game other)
		{
			if (other.IsFavourite) keeper.IsFavourite = true;

			if (string.IsNullOrEmpty(keeper.InstallRoot) && !string.IsNullOrEmpty(other.InstallRoot))
				keeper.InstallRoot = other.InstallRoot;

			foreach (var session in other.Sessions)
			{
				// Ugyanaz a futás, ha a kezdés (közel) azonos - ekkor nem számoljuk kétszer.
				var same = keeper.Sessions.FirstOrDefault(k =>
					Math.Abs((k.StartedAt - session.StartedAt).TotalSeconds) < 2);

				if (same == null)
				{
					keeper.Sessions.Add(session);
				}
				else
				{
					if (session.EndedAt > same.EndedAt) same.EndedAt = session.EndedAt;
					same.IsOpen = same.IsOpen || session.IsOpen;
				}
			}

			keeper.Sessions.Sort((a, b) => a.StartedAt.CompareTo(b.StartedAt));
		}

		// -------------------------------------------------------------------
		// TÖRLÉS / VISSZATÖLTÉS ÉSZLELÉS
		// -------------------------------------------------------------------

		private async Task RunAvailabilityCheckAsync()
		{
			if (_disposed) return;
			// Ha az előző ellenőrzés még tart (lassú / lecsatolt lemez), kihagyjuk ezt a tick-et.
			if (Interlocked.Exchange(ref _availabilityCheckRunning, 1) == 1) return;

			try
			{
				var snapshot = SnapshotForAvailability();
				var exists = await Task.Run(() => snapshot.Select(x => File.Exists(x.Path)).ToList());

				// Az await után a UI szálon vagyunk (DispatcherSynchronizationContext).
				if (_disposed) return;
				ApplyAvailability(snapshot, exists);
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[GameService] Availability check hiba: {ex.Message}");
			}
			finally
			{
				Volatile.Write(ref _availabilityCheckRunning, 0);
			}
		}

		private List<(Game Game, string Path)> SnapshotForAvailability()
		{
			return Games
				.Where(g => !g.IsExcluded)
				.Select(g => (g, g.Source))
				.ToList();
		}

		private void ApplyAvailability(List<(Game Game, string Path)> snapshot, List<bool> exists)
		{
			for (int i = 0; i < snapshot.Count && i < exists.Count; i++)
			{
				var (game, path) = snapshot[i];

				// Közben módosult (pl. Change exe path / Exclude) -> az eredmény elavult.
				if (game.IsExcluded) continue;
				if (!string.Equals(game.Source, path, StringComparison.OrdinalIgnoreCase)) continue;

				bool shouldBeDeleted = !exists[i];
				if (game.IsDeleted != shouldBeDeleted)
					game.IsDeleted = shouldBeDeleted;   // a PropertyChanged úgyis kér mentést
			}
		}

		/// <summary>Bármely szálról hívható (a fájlellenőrzés a hívó szálon, a módosítás a UI szálon fut).</summary>
		public void MarkMissingGamesAsDeleted() => SyncAvailabilityBlocking();

		/// <summary>Bármely szálról hívható.</summary>
		public void RestoreReappearedGames() => SyncAvailabilityBlocking();

		private void SyncAvailabilityBlocking()
		{
			try
			{
				var snapshot = OnUiResult(SnapshotForAvailability);
				var exists = snapshot.Select(x => File.Exists(x.Path)).ToList();
				OnUi(() => ApplyAvailability(snapshot, exists));
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[GameService] SyncAvailability hiba: {ex.Message}");
			}
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

		// -------------------------------------------------------------------
		// KIZÁRÁS / EXE CSERE
		// -------------------------------------------------------------------

		/// <summary>
		/// A jatekot "kizartra" allitja: mostantol nem jelenik meg a Libraryban,
		/// es a jelenlegi Source path-ja felkerul a kizart utak listajara, hogy
		/// egyetlen jovobeli scan se hozza vissza/hozza letre ujra ugyanezen
		/// az uton. Maga a rekord (statisztikak, session-ok) megmarad a
		/// Games.json-ban. (Az esetleg futó sessiont a monitor zárja le.)
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

			// A felhasználó most kifejezetten ezt az exe-t választotta: ha korábban kizárt
			// útvonal volt (pl. visszaváltás az eredeti exe-re), vegyük le a tiltólistáról.
			_settingsService.Current.ExcludedGamePaths.RemoveAll(p =>
				string.Equals(p, newExePath, StringComparison.OrdinalIgnoreCase));

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

		// -------------------------------------------------------------------
		// MENTÉS
		// -------------------------------------------------------------------

		private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
		{
			if (e.NewItems != null)
			{
				foreach (Game game in e.NewItems)
					SubscribeToGame(game);
			}
			if (e.OldItems != null)
			{
				foreach (Game game in e.OldItems)
					game.PropertyChanged -= OnGamePropertyChanged;
			}
			RequestSave();
		}

		private void SubscribeToGame(Game game)
		{
			game.PropertyChanged += OnGamePropertyChanged;
		}

		private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName != null && PersistedProperties.Contains(e.PropertyName))
				RequestSave();
		}

		/// <summary>Késleltetett (1 mp-es debounce) mentés. Bármely szálról hívható.</summary>
		public void RequestSave()
		{
			if (_disposed) return;

			if (!_dispatcher.CheckAccess())
			{
				_dispatcher.BeginInvoke(new Action(RequestSave));
				return;
			}

			_saveTimer.Stop();
			_saveTimer.Start();
		}

		/// <summary>
		/// Azonnali mentés. Bármely szálról hívható: a szerializálás mindig a UI szálon történik,
		/// így közben egyetlen Sessions lista sem módosulhat. Hiba esetén nem dob kivételt, hanem újrapróbálja.
		/// </summary>
		public void Save()
		{
			if (!_dispatcher.CheckAccess())
			{
				try { _dispatcher.Invoke(new Action(Save)); }
				catch (Exception ex) { Debug.WriteLine($"[GameService] Save marshal hiba: {ex.Message}"); }
				return;
			}

			_saveTimer.Stop();

			try
			{
				_storage.SaveGames(Games.ToList());
			}
			catch (Exception ex)
			{
				Debug.WriteLine($"[GameService] Mentés sikertelen, újrapróbáljuk: {ex.Message}");
				if (!_disposed) _saveTimer.Start();
			}
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;

			_availabilityTimer.Stop();
			_saveTimer.Stop();
		}
	}
}


