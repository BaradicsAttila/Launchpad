using System;
using System.Text.Json.Serialization;

namespace LaunchPad.Model
{
	public class Session
	{
		public DateTime StartedAt { get; set; }

		/// <summary>
		/// Futás közben a monitor folyamatosan előre tolja ("utoljára láttuk futni").
		/// Ha az app összeomlik, ez az érték marad a lemezen = a session valódi vége.
		/// </summary>
		public DateTime EndedAt { get; set; }

		/// <summary>
		/// True, amíg a session nem zárult le rendesen. LEMEZRE IS KERÜL, így egy
		/// összeomlás / kilövés után induláskor felismerhető a "félbehagyott" session.
		/// A régi Games.json-ban nincs ilyen mező -> false (lezárt), ami helyes.
		/// </summary>
		public bool IsOpen { get; set; }

		[JsonIgnore]
		public int DurationSeconds => Math.Max(0, (int)(EndedAt - StartedAt).TotalSeconds);
	}
}
