using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using LaunchPad.Helpers;
using LaunchPad.Model;

namespace LaunchPad.Services
{
	public enum LaunchOutcome
	{
		Started,
		AlreadyRunning,
		FileMissing,
		Cancelled,   // pl. a felhasználó nemet mondott az UAC kérdésre
		Failed
	}

	public sealed record LaunchResult(LaunchOutcome Outcome, string? Message = null);

	/// <summary>
	/// Csak elindítja a játékot. A session nyitását/zárását NEM ez végzi, hanem a GameProcessMonitor,
	/// ezért az app-on kívülről (Steam, Explorer, parancsikon) indított játék is ugyanúgy követve lesz.
	/// A UI szálról használandó.
	/// </summary>
	public class GameLauncher
	{
		// Dupla kattintás ellen: a monitor csak pár másodperc múlva látja meg a processzt.
		private static readonly TimeSpan LaunchCooldown = TimeSpan.FromSeconds(8);

		private readonly GameProcessMonitor _monitor;
		private readonly Dictionary<Game, DateTime> _recentLaunches = new();

		public GameLauncher(GameProcessMonitor monitor)
		{
			_monitor = monitor;
		}

		public LaunchResult Launch(Game game)
		{
			if (game.IsRunning)
				return new LaunchResult(LaunchOutcome.AlreadyRunning);

			if (_recentLaunches.TryGetValue(game, out var launchedAt) &&
				DateTime.UtcNow - launchedAt < LaunchCooldown)
				return new LaunchResult(LaunchOutcome.AlreadyRunning);

			var exe = PathHelper.NormalizeFile(game.Source);
			if (exe == null || !File.Exists(exe))
			{
				game.IsDeleted = true;
				return new LaunchResult(LaunchOutcome.FileMissing,
					$"A játék fájlja nem található:\n{game.Source}");
			}

			try
			{
				var startInfo = new ProcessStartInfo
				{
					FileName = exe,
					// Sok játék a saját mappájából keresi az erőforrásait.
					WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty,
					// ShellExecute: az admin jogot kérő (manifest) játékoknál is megjelenik az UAC kérdés.
					UseShellExecute = true
				};

				using var process = Process.Start(startInfo);
			}
			catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
			{
				// ERROR_CANCELLED: az UAC-t elutasították, ez nem hiba.
				return new LaunchResult(LaunchOutcome.Cancelled);
			}
			catch (Exception ex)
			{
				return new LaunchResult(LaunchOutcome.Failed,
					$"A játék nem indítható:\n{ex.Message}");
			}

			_recentLaunches[game] = DateTime.UtcNow;
			_monitor.RequestFastPoll();

			return new LaunchResult(LaunchOutcome.Started);
		}

		/// <summary>Mint a Launch, de hiba esetén üzenetet is mutat.</summary>
		public LaunchResult LaunchWithFeedback(Game game)
		{
			var result = Launch(game);

			if (result.Outcome is LaunchOutcome.FileMissing or LaunchOutcome.Failed)
			{
				MessageBox.Show(
					result.Message ?? "Ismeretlen hiba.",
					"LaunchPad",
					MessageBoxButton.OK,
					MessageBoxImage.Warning);
			}

			return result;
		}
	}
}
