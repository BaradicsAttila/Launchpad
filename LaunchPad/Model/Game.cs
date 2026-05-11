using System;
using System.Collections.Generic;
using System.Text.Json;
using System.IO;

namespace LaunchPad.Model
{
    class Game
    {
        public string Name { get; private set; }
        public string IconSource { get; private set; }
        public bool IsFavourite {  get; private set; }
        public int PlaytimeMinutes { get; private set; }
        public int LastPlayedHours { get; private set; }
        public int LaunchCount { get; private set; }
        public bool IsGame {  get; private set; }
        public string Source { get; private set; }
        public bool IsDeleted { get; private set; }

    }
}
