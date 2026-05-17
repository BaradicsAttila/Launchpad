using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Linq;

namespace LaunchPad.Model
{
    public partial class Game : ObservableObject
    {
        public string Name { get; init; }

        [ObservableProperty]
        private string _source;
        [ObservableProperty]
        private bool _isFavourite;
        [ObservableProperty]
        private bool _isDeleted;
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
		//public DateTime? LastPlayedUtc
		//{
		//	get
		//	{
		//		if (Sessions.Any())
		//			return Sessions.Max(s => s.EndedAt);
		//		else
		//			return null;
		//	}
		//}

	}
}
