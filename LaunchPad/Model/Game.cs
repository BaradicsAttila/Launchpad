using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using LaunchPad.Helpers;

namespace LaunchPad.Model
{
	public partial class Game : ObservableObject
	{
		public string Name { get; init; }

		[ObservableProperty]
		private string source;
		[ObservableProperty]
		private bool isFavourite;
		[ObservableProperty]
		private bool isDeleted;
		[ObservableProperty]
		private bool _isExcluded;

		/// <summary>
		/// A játék telepítési mappája. A GameProcessMonitor ez alapján ismeri fel a futását:
		/// bármelyik, ez alatt futó (nem telepítő/segéd) processz = a játék fut.
		/// Null esetén csak a pontos Source exe-t figyeljük.
		/// </summary>
		[ObservableProperty]
		private string? installRoot;

		public List<Session> Sessions { get; set; } = new();
		[JsonIgnore]
		public int TotalPlaytimeSeconds => Sessions.Sum(s => s.DurationSeconds);
		[JsonIgnore]
		public int LaunchCount => Sessions.Count;
		[JsonIgnore]
		public DateTime? LastPlayedUtc => Sessions.Any()
			? Sessions.Max(s => s.EndedAt)
			: null;

		[JsonIgnore]
		public string Platform => DetectPlatform(Source);

		private static string DetectPlatform(string? path)
		{
			if (string.IsNullOrEmpty(path)) return "Other";
			var lower = path.ToLowerInvariant();

			if (lower.Contains(@"\steamapps\")) return "Steam";
			if (lower.Contains(@"\epic games\")) return "Epic";
			if (lower.Contains(@"\ubisoft game launcher\")) return "Ubisoft";
			if (lower.Contains(@"\ea games\") || lower.Contains(@"\origin games\")) return "EA";
			if (lower.Contains(@"\gog galaxy\")) return "GOG";
			if (lower.Contains("battle.net") || lower.Contains("blizzard")) return "Battle.net";
			return "Other";
		}
		[JsonIgnore]
		public string LastPlayedFormatted
		{
			get
			{
				if (LastPlayedUtc == null) return "Never";
				int difference = (int)(DateTime.UtcNow - LastPlayedUtc.Value).TotalSeconds;
				string formatted = difference switch
				{
					< 60 => "Just now",
					< 3600 => "Less than an hour ago",
					< 86400 => $"{difference / 3600} hour{(difference / 3600 == 1 ? "" : "s")} ago",
					< 2592000 => $"{difference / 86400} day{(difference / 86400 == 1 ? "" : "s")} ago",
					< 31536000 => $"{difference / 2592000} month{(difference / 2592000 == 1 ? "" : "s")} ago",
					_ => $"{difference / 31536000} year{(difference / 31536000 == 1 ? "" : "s")} ago"

				};
				return formatted;
			}
		}
		[JsonIgnore]
		public int AverageSessionSeconds
		{
			get
			{
				if (!Sessions.Any()) return 0;
				return (int)Sessions.Average(s => s.DurationSeconds);
			}
		}
		[JsonIgnore]
		public int Last24hPlaytimeSeconds
		{
			get
			{
				if (!Sessions.Any()) return 0;
				DateTime lastday = DateTime.UtcNow.AddDays(-1);
				return Sessions
					.Where(s => s.EndedAt >= lastday)
					.Sum(s => s.DurationSeconds);
			}
		}
		[JsonIgnore]
		public int LastWeekPlaytimeSeconds
		{
			get
			{
				if (!Sessions.Any()) return 0;
				DateTime lastweek = DateTime.UtcNow.AddDays(-7);
				return Sessions
					.Where(s => s.EndedAt >= lastweek)
					.Sum(s => s.DurationSeconds);
			}
		}
		[JsonIgnore]
		public int LastMonthPlaytimeSeconds
		{
			get
			{
				if (!Sessions.Any()) return 0;
				DateTime lastmonth = DateTime.UtcNow.AddMonths(-1);
				return Sessions
					.Where(s => s.EndedAt >= lastmonth)
					.Sum(s => s.DurationSeconds);
			}
		}
		[JsonIgnore]
		public int LastYearPlaytimeSeconds
		{
			get
			{
				if (!Sessions.Any()) return 0;
				DateTime lastyear = DateTime.UtcNow.AddYears(-1);
				return Sessions
					.Where(s => s.EndedAt >= lastyear)
					.Sum(s => s.DurationSeconds);
			}
		}
		[JsonIgnore]
		public bool? IsGame => DetermineIsGame(Source);
		private ImageSource? _icon;
		private bool _iconLoaded;

		[JsonIgnore]
		public ImageSource? Icon
		{
			get
			{
				if (!_iconLoaded)
				{
					_icon = LaunchPad.Helpers.IconHelper.GetIcon(Source, 128);
					_iconLoaded = true;
				}
				return _icon;
			}
		}

		/// <summary>A jelenleg futó session (nem szerializálódik, a monitor állítja vissza induláskor az IsOpen alapján).</summary>
		[JsonIgnore]
		public Session? ActiveSession { get; private set; }

		/// <summary>Igaz, amíg a játék fut (van aktív session). Bindolható a UI-ban.</summary>
		[JsonIgnore]
		public bool IsRunning => ActiveSession != null;

		[JsonIgnore]
		public string LastPlayedLabel => LastPlayedUtc.HasValue
			? $"Played {this.LastPlayedFormatted}"
			: "Never played";

		partial void OnSourceChanged(string value)
		{
			_iconLoaded = false;
			OnPropertyChanged(nameof(Icon));
			OnPropertyChanged(nameof(IsGame));
			OnPropertyChanged(nameof(Platform));

			// Ha az exe kikerült a telepítési mappából (pl. "Change exe path"),
			// a régi InstallRoot hibás lenne - inkább eldobjuk, a scan újra kitöltheti.
			if (!string.IsNullOrEmpty(InstallRoot) && !PathHelper.IsUnderDirectory(InstallRoot, value))
				InstallRoot = null;
		}

		// -------------------------------------------------------------------
		// SESSION KEZELÉS - kizárólag a UI szálról hívandó (a monitor is oda dispatch-el)
		// -------------------------------------------------------------------

		/// <summary>Új, nyitott session. A StartedAt a processz valódi indulása, a "most" a mostani idő.</summary>
		public Session StartSession(DateTime startedUtc, DateTime nowUtc)
		{
			if (ActiveSession != null) return ActiveSession;

			if (startedUtc > nowUtc) startedUtc = nowUtc;

			var session = new Session
			{
				StartedAt = startedUtc,
				EndedAt = nowUtc,
				IsOpen = true,
			};
			Sessions.Add(session);
			ActiveSession = session;
			RefreshDerivedProperties();
			return session;
		}

		/// <summary>Lemezről betöltött, még nyitott sessiont aktívvá tesz (újraindítás / összeomlás utáni helyreállítás).</summary>
		public void ResumeSession(Session session)
		{
			if (ActiveSession != null) return;

			if (session.EndedAt < session.StartedAt) session.EndedAt = session.StartedAt;
			session.IsOpen = true;
			ActiveSession = session;
			RefreshDerivedProperties();
		}

		/// <summary>"Még fut": az EndedAt-et előre tolja. Soha nem viszi vissza az időt.</summary>
		public void FlushSession(DateTime nowUtc)
		{
			if (ActiveSession == null) return;

			if (nowUtc > ActiveSession.EndedAt)
				ActiveSession.EndedAt = nowUtc;
			RefreshDerivedProperties();
		}

		/// <summary>
		/// Lezárja az aktív sessiont a megadott időpontban. Ha az eredmény rövidebb, mint
		/// discardIfShorterThanSeconds, a sessiont teljesen eldobja (azonnal összeomló indítás = nem játék).
		/// </summary>
		public void EndSession(DateTime endedUtc, int discardIfShorterThanSeconds = 0)
		{
			var session = ActiveSession;
			if (session == null) return;

			if (endedUtc < session.StartedAt) endedUtc = session.StartedAt;

			session.EndedAt = endedUtc;
			session.IsOpen = false;
			ActiveSession = null;

			if (discardIfShorterThanSeconds > 0 && session.DurationSeconds < discardIfShorterThanSeconds)
				Sessions.Remove(session);

			RefreshDerivedProperties();
		}

		private void RefreshDerivedProperties()
		{
			OnPropertyChanged(nameof(IsRunning));
			OnPropertyChanged(nameof(TotalPlaytimeSeconds));
			OnPropertyChanged(nameof(LaunchCount));
			OnPropertyChanged(nameof(LastPlayedUtc));
			OnPropertyChanged(nameof(LastPlayedFormatted));
			OnPropertyChanged(nameof(LastPlayedLabel));
			OnPropertyChanged(nameof(AverageSessionSeconds));
			OnPropertyChanged(nameof(Last24hPlaytimeSeconds));
			OnPropertyChanged(nameof(LastWeekPlaytimeSeconds));
			OnPropertyChanged(nameof(LastMonthPlaytimeSeconds));
			OnPropertyChanged(nameof(LastYearPlaytimeSeconds));
		}
		private static readonly string[] GamePlatformPaths = new[]
		{
			"steam", "steamapps", "epic games", "epicgames",
			"ubisoft", "uplay", "origin", "ea games", "ea desktop",
			"gog galaxy", "gog.com", "battle.net", "battlenet",
			"rockstar games", "riot games", "2k games", "blizzard"
		};

		private static bool? DetermineIsGame(string? exepath)
		{
			if (string.IsNullOrEmpty(exepath)) return null;
			string lower = exepath.ToLowerInvariant();
			return GamePlatformPaths.Any(p => lower.Contains(p));
		}
		private static ImageSource? ExtractIcon(string? exePath)
		{
			if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath))
				return null;

			try
			{
				using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
				if (icon == null) return null;

				return Imaging.CreateBitmapSourceFromHIcon(
					icon.Handle,
					System.Windows.Int32Rect.Empty,
					BitmapSizeOptions.FromEmptyOptions());
			}
			catch { return null; }
		}
		[JsonConstructor]
		public Game(string name, string source, bool isFavourite, bool isDeleted, List<Session>? sessions)
		{
			Name = name;
			this.source = source;
			this.isFavourite = isFavourite;
			this.isDeleted = isDeleted;
			Sessions = sessions ?? new List<Session>();
		}


	}
}
