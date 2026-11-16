using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using LaunchPad.Helpers;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	/// <summary>
	/// Háttérben figyeli a futó processzeket, és ez alapján nyitja/zárja a játékok sessionjeit.
	/// Nem tudja, ki indította a játékot (az app vagy a Steam/Explorer), ezért mindkettőt egyformán követi.
	///
	/// Garanciák:
	///  - a session EndedAt-je mindig az UTOLSÓ látott futás ideje, tehát a türelmi idő és az
	///    app-összeomlás sem tolja meg a játékidőt;
	///  - az IsOpen flag lemezre kerül, így összeomlás / kilövés után induláskor a nyitott session
	///    vagy folytatódik (ha a játék még fut), vagy az utolsó mentett időpontnál lezáródik;
	///  - ha a processzlista lekérdezése hibás, a kör kimarad, és semmi sem változik;
	///  - a Games és a Sessions kizárólag a UI szálon módosul.
	/// </summary>
	public sealed class GameProcessMonitor : IDisposable
	{
		// ------------------------------------------------------------------
		// BEÁLLÍTÁSOK
		// ------------------------------------------------------------------

		private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(4);

		// Indítás után egy ideig sűrűbben nézünk, hogy a session gyorsan megjelenjen.
		private static readonly TimeSpan FastPollInterval = TimeSpan.FromSeconds(1);
		private static readonly TimeSpan FastPollDuration = TimeSpan.FromSeconds(45);

		// Ennyi ideig nem látott játékot zárunk le. Elnyeli a launcher->játék átadást
		// (pl. Steam újraindítja az exe-t), hogy ne legyen két külön session.
		private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(12);

		// Két kör között ennyinél több idő telt el = a gép aludt / hibernált (a poll 4 mp).
		private static readonly TimeSpan SleepGapThreshold = TimeSpan.FromSeconds(25);

		// Futás közben legfeljebb ennyi ideje lehet a legutóbbi mentés (ennyit veszíthetünk összeomláskor).
		private static readonly TimeSpan SaveInterval = TimeSpan.FromSeconds(10);

		// Újraindítás után: ha a játék legkorábbi processze ennyivel az utolsó mentett
		// EndedAt után indult, az már egy MÁSIK futás, nem a megszakadt session folytatása.
		private static readonly TimeSpan ResumeTolerance = TimeSpan.FromSeconds(5);

		// Ennél rövidebb sessiont eldobunk (azonnal összeomló / megszakított indítás).
		private const int MinSessionSeconds = 10;

		// ------------------------------------------------------------------

		private sealed record Matcher(Game Game, string ExactPath, string? Root, string FileName);

		private enum Change { None, Flushed, Structural }

		private readonly GameService _gameService;
		private readonly Dispatcher _dispatcher;
		private readonly ProcessScanner _scanner = new();

		// Csak a UI szálról érhető el.
		private readonly HashSet<Game> _unvalidated = new();
		private DateTime _lastTickUtc;
		private DateTime _lastSaveUtc;

		private readonly SemaphoreSlim _wake = new(0, 1);
		private CancellationTokenSource? _cts;
		private Task? _loop;
		private long _fastUntilTicks;
		private volatile bool _stopped;

		public GameProcessMonitor(GameService gameService)
		{
			_gameService = gameService;
			_dispatcher = Application.Current.Dispatcher;
		}

		// ------------------------------------------------------------------
		// ÉLETCIKLUS
		// ------------------------------------------------------------------

		/// <summary>Az App.OnStartup-ból hívandó, a scan és a MarkMissingGamesAsDeleted UTÁN.</summary>
		public void Start()
		{
			if (!_dispatcher.CheckAccess())
			{
				_dispatcher.Invoke(new Action(Start));
				return;
			}

			if (_loop != null || _stopped) return;

			RecoverOpenSessions();

			_lastTickUtc = DateTime.UtcNow;
			_lastSaveUtc = _lastTickUtc;

			_cts = new CancellationTokenSource();
			var token = _cts.Token;
			_loop = Task.Run(() => RunLoopAsync(token));
		}

		/// <summary>
		/// Az App.OnExit-ből hívandó. NEM zárja le a futó játék sessionjét (a játék az app után is futhat),
		/// csak előre tolja az EndedAt-jét és elmenti. A nem futó játékok sessionjeit lezárja.
		/// Ha a játék közben is fut, a következő indításkor a Start() folytatja a sessiont.
		/// </summary>
		public void StopAndPersist()
		{
			if (!_dispatcher.CheckAccess())
			{
				_dispatcher.Invoke(new Action(StopAndPersist));
				return;
			}

			if (_stopped) return;
			_stopped = true;

			try { _cts?.Cancel(); } catch { }

			try
			{
				var matchers = BuildMatchers();
				var processes = _scanner.Snapshot();
				var running = Match(matchers, processes);
				Apply(running, DateTime.UtcNow, isFinal: true);
			}
			catch (Exception ex)
			{
				// Nem sikerült a végső pillanatkép: a nyitott sessionök nyitva maradnak az utolsó
				// biztos EndedAt-tel, és a következő indításkor a RecoverOpenSessions rendezi őket.
				Debug.WriteLine($"[GameProcessMonitor] Végső pillanatkép hiba: {ex.Message}");
			}
			finally
			{
				_gameService.Save();
			}
		}

		public void Dispose()
		{
			_stopped = true;
			try { _cts?.Cancel(); } catch { }
		}

		/// <summary>Indítás után sűrűbb figyelés + azonnali kör, hogy a session gyorsan megjelenjen.</summary>
		public void RequestFastPoll()
		{
			Interlocked.Exchange(ref _fastUntilTicks, (DateTime.UtcNow + FastPollDuration).Ticks);

			if (_wake.CurrentCount == 0)
			{
				try { _wake.Release(); }
				catch (SemaphoreFullException) { }
				catch (ObjectDisposedException) { }
			}
		}

		private bool IsFastPollActive() => DateTime.UtcNow.Ticks < Interlocked.Read(ref _fastUntilTicks);

		// ------------------------------------------------------------------
		// HELYREÁLLÍTÁS INDULÁSKOR (UI szál)
		// ------------------------------------------------------------------

		/// <summary>
		/// A lemezről betöltött, még IsOpen sessionöket aktívvá teszi. Hogy valóban fut-e még a játék,
		/// az első kör dönti el: ha igen, folytatódik; ha nem, az utolsó mentett EndedAt-nél lezáródik.
		/// </summary>
		private void RecoverOpenSessions()
		{
			bool changed = false;

			foreach (var game in _gameService.Games.ToList())
			{
				var open = game.Sessions
					.Where(s => s.IsOpen)
					.OrderBy(s => s.StartedAt)
					.ToList();

				if (open.Count == 0) continue;

				// Egy játéknál legfeljebb egy nyitott session lehet; a többit (sérült adat) lezárjuk.
				for (int i = 0; i < open.Count - 1; i++)
				{
					open[i].IsOpen = false;
					if (open[i].EndedAt < open[i].StartedAt) open[i].EndedAt = open[i].StartedAt;
					changed = true;
				}

				game.ResumeSession(open[^1]);
				_unvalidated.Add(game);
			}

			if (changed) _gameService.Save();
		}

		// ------------------------------------------------------------------
		// FŐ CIKLUS (háttérszál)
		// ------------------------------------------------------------------

		private async Task RunLoopAsync(CancellationToken ct)
		{
			while (!ct.IsCancellationRequested)
			{
				try
				{
					await TickAsync(ct).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (Exception ex)
				{
					// Egy hibás kör nem állíthatja le a figyelést.
					Debug.WriteLine($"[GameProcessMonitor] Tick hiba: {ex.Message}");
				}

				var delay = IsFastPollActive() ? FastPollInterval : PollInterval;

				try
				{
					await _wake.WaitAsync(delay, ct).ConfigureAwait(false);
				}
				catch (OperationCanceledException)
				{
					break;
				}
				catch (ObjectDisposedException)
				{
					break;
				}
			}
		}

		private async Task TickAsync(CancellationToken ct)
		{
			if (_stopped) return;

			// 1) A játékok adatai a UI szálról (a Game mezőit csak ott olvassuk).
			var matchers = await _dispatcher.InvokeAsync(
				() => BuildMatchers(), DispatcherPriority.Normal, ct);

			// 2) Processzlista (háttérszál). Hiba esetén kivétel -> a kör kimarad, semmi nem változik.
			var processes = _scanner.Snapshot();
			var nowUtc = DateTime.UtcNow;

			// 3) Illesztés (tiszta számítás, nincs mellékhatás).
			var running = Match(matchers, processes);

			// 4) Állapot frissítése a UI szálon.
			await _dispatcher.InvokeAsync(
				() => Apply(running, nowUtc, isFinal: false), DispatcherPriority.Normal, ct);
		}

		// ------------------------------------------------------------------
		// ILLESZTÉS
		// ------------------------------------------------------------------

		private List<Matcher> BuildMatchers()
		{
			var list = new List<Matcher>();

			foreach (var game in _gameService.Games)
			{
				// A kizárt / hiányzó játékot nem követjük. Ha közben nyitott sessionje van,
				// az "nem fut" ágra kerül és türelmi idő után lezáródik.
				if (game.IsExcluded || game.IsDeleted) continue;

				var exact = PathHelper.NormalizeFile(game.Source);
				if (exact == null) continue;

				var root = PathHelper.NormalizeInstallRoot(game.InstallRoot);
				list.Add(new Matcher(game, exact, root, Path.GetFileName(exact)));
			}

			return list;
		}

		/// <summary>Játék -> a legkorábbi illeszkedő processz indulási ideje (null, ha ismeretlen). Ami nincs a kulcsok közt, nem fut.</summary>
		private static Dictionary<Game, DateTime?> Match(List<Matcher> matchers, List<RunningProcess> processes)
		{
			var result = new Dictionary<Game, DateTime?>();

			foreach (var process in processes)
			{
				foreach (var matcher in matchers)
				{
					if (!IsMatch(matcher, process)) continue;

					if (result.TryGetValue(matcher.Game, out var existing))
					{
						if (process.StartUtc.HasValue && (!existing.HasValue || process.StartUtc < existing))
							result[matcher.Game] = process.StartUtc;
					}
					else
					{
						result[matcher.Game] = process.StartUtc;
					}
				}
			}

			return result;
		}

		private static bool IsMatch(Matcher matcher, RunningProcess process)
		{
			// Ha az útvonal nem kérdezhető le (pl. védett processz), a fájlnév egyezése is elég
			// a Source exe-hez. Ez véd attól, hogy egy anti-cheattel védett játékot "kilépettnek" lássunk.
			if (process.Path == null)
				return string.Equals(process.ExeName, matcher.FileName, StringComparison.OrdinalIgnoreCase);

			// A beállított exe mindig illeszkedik.
			if (string.Equals(process.Path, matcher.ExactPath, StringComparison.OrdinalIgnoreCase))
				return true;

			// A telepítési mappa alatti többi processz is (pl. Unreal "-Shipping.exe"),
			// kivéve a telepítő / crash handler / redist jellegűeket.
			if (matcher.Root != null &&
				process.Path.StartsWith(matcher.Root, StringComparison.OrdinalIgnoreCase) &&
				!GameScanner.IsLikelyNonGameExecutable(process.Path))
				return true;

			return false;
		}

		// ------------------------------------------------------------------
		// ÁLLAPOTGÉP (UI szál)
		// ------------------------------------------------------------------

		private void Apply(Dictionary<Game, DateTime?> running, DateTime nowUtc, bool isFinal)
		{
			if (_stopped && !isFinal) return;

			bool gap = !isFinal && (nowUtc - _lastTickUtc) > SleepGapThreshold;
			bool structural = false;
			bool flushed = false;

			foreach (var game in _gameService.Games.ToList())
			{
				try
				{
					switch (ApplyToGame(game, running, nowUtc, gap, isFinal))
					{
						case Change.Structural: structural = true; break;
						case Change.Flushed: flushed = true; break;
					}
				}
				catch (Exception ex)
				{
					// Egy játék hibája ne akadályozza a többit.
					Debug.WriteLine($"[GameProcessMonitor] {game.Name}: {ex.Message}");
				}
			}

			_lastTickUtc = nowUtc;

			// Indulás / leállás: azonnal mentünk. Futás közben: legfeljebb SaveInterval-onként.
			if (structural || (flushed && nowUtc - _lastSaveUtc >= SaveInterval))
			{
				_gameService.Save();
				_lastSaveUtc = nowUtc;
			}
		}

		private Change ApplyToGame(
			Game game,
			Dictionary<Game, DateTime?> running,
			DateTime now,
			bool gap,
			bool isFinal)
		{
			var active = game.ActiveSession;
			bool isRunning = running.TryGetValue(game, out var processStart);

			// 1) A gép aludt / hibernált: az alvás nem játékidő. A sessiont az utolsó ismert
			//    EndedAt-nél lezárjuk, és ha a játék még fut, újat nyitunk "most"-tól.
			if (gap && active != null)
			{
				CloseSession(game, active.EndedAt);

				if (isRunning)
					game.StartSession(now, now);

				return Change.Structural;
			}

			if (isRunning)
			{
				// 2) Fut, de nincs session -> új session a processz valódi indulásától.
				if (active == null)
				{
					game.StartSession(ResolveStart(game, processStart, now), now);
					return Change.Structural;
				}

				// 3) Újraindítás után az első találkozás egy lemezről folytatott sessionnel:
				//    ha a processz az utolsó mentett EndedAt UTÁN indult, az egy másik futás.
				if (_unvalidated.Remove(game) &&
					processStart.HasValue &&
					processStart.Value > active.EndedAt + ResumeTolerance)
				{
					CloseSession(game, active.EndedAt);
					game.StartSession(ResolveStart(game, processStart, now), now);
					return Change.Structural;
				}

				// 4) Fut és van session -> "még él".
				game.FlushSession(now);
				return Change.Flushed;
			}

			// 5) Nem fut. Az EndedAt az utolsó látott futás ideje, ezért a türelmi idő nem számít bele.
			if (active != null && (isFinal || now - active.EndedAt >= StopGrace))
			{
				CloseSession(game, active.EndedAt);
				return Change.Structural;
			}

			return Change.None;
		}

		private void CloseSession(Game game, DateTime endedUtc)
		{
			_unvalidated.Remove(game);
			game.EndSession(endedUtc, MinSessionSeconds);
		}

		/// <summary>
		/// Új session kezdete: a processz indulása, de nem korábbi, mint az előző session vége
		/// (így két session sosem fedi át egymást), és nem a jövőben.
		/// </summary>
		private static DateTime ResolveStart(Game game, DateTime? processStart, DateTime now)
		{
			var start = processStart ?? now;

			var lastEnd = game.Sessions
				.Where(s => !s.IsOpen)
				.Select(s => s.EndedAt)
				.DefaultIfEmpty(DateTime.MinValue)
				.Max();

			if (start < lastEnd) start = lastEnd;
			if (start > now) start = now;

			return start;
		}
	}
}
