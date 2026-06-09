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
		[JsonIgnore]
		public ImageSource? Icon => ExtractIcon(Source);
		[JsonIgnore]
		public Session? ActiveSession { get; private set; }
		[JsonIgnore]
		public string LastPlayedLabel => LastPlayedUtc.HasValue
			? $"Played {this.LastPlayedFormatted}"
			: "Never played";

		partial void OnSourceChanged(string value)
		{
			OnPropertyChanged(nameof(Icon));
			OnPropertyChanged(nameof(IsGame));
		}
		public void StartSession()
		{
			var session = new Session
			{
				StartedAt = DateTime.UtcNow,
				EndedAt = DateTime.UtcNow,
			};
			Sessions.Add(session);
			ActiveSession = session;
			RefreshDerivedProperties();
		}
		public void EndSession()
		{
			if (ActiveSession == null) return;
			ActiveSession.EndedAt = DateTime.UtcNow;
			ActiveSession = null;
			RefreshDerivedProperties();
		}
		public void backupsession()
		{
			if (ActiveSession == null) return;
			ActiveSession.EndedAt = DateTime.UtcNow;
			RefreshDerivedProperties();
		}
		private void RefreshDerivedProperties()
		{
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