using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Serialization;

namespace LaunchPad.Model
{
	public class Session
	{
		public DateTime StartedAt { get; set; }
		public DateTime EndedAt { get; set; }
		[JsonIgnore]
		public int DurationSeconds => (int)(EndedAt-StartedAt).TotalSeconds;
	}
}
