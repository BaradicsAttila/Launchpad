using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaunchPad.Model
{
    class Game
    {
        public string Name { get; init; }
        public string IconSource { get; init; }
        public bool IsFavourite {  get; init; }
        public int PlaytimeHours { get; init; }
        public int LastPlayedHours { get; init; }
        public int LaunchCount { get; init; }
        public bool IsGame {  get; init; }
        public string Source { get; init; }
        public bool IsDeleted { get; init; }

        [JsonConstructor]
        public Game(
            string name, 
            string iconSource, 
            bool isFavourite,
            int playtimeHours, 
            int lastPlayedHours, 
            int launchCount,
            bool isGame, 
            string source, 
            bool isDeleted)
        {
            Name = name;
            IconSource = iconSource;
            IsFavourite = isFavourite;
            PlaytimeHours = playtimeHours;
            LastPlayedHours = lastPlayedHours;
            LaunchCount = launchCount;
            IsGame = isGame;
            Source = source;
            IsDeleted = isDeleted;
        }
    }
}
