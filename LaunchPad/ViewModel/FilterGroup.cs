using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LaunchPad.ViewModel
{
	/// <summary>
	/// Egy "All" kapcsolóból és chipekből álló szűrőcsoport (SHOW és SOURCE is ezt használja).
	///
	/// - All = egyetlen chip sincs kijelölve (nincs szűrés)
	/// - All bekapcsolva: az összes chip kikapcsol (az előző állapotot elmentjük)
	/// - All kikapcsolva: visszajönnek a mentett chipek
	/// - Chipre kattintás, miközben az All be van kapcsolva: csak az a chip marad, az All kikapcsol
	/// - Az utolsó chip kivétele, vagy mind bepipálása: All bekapcsol, a chipek kiürülnek
	/// </summary>
	public partial class FilterGroup : ObservableObject
	{
		private bool _suppressSync;
		private List<bool>? _snapshot;

		public ObservableCollection<FilterOption> Options { get; } = new();

		[ObservableProperty]
		private bool isAll;

		/// <summary>Akkor sül el, ha a csoport kijelölése megváltozott.</summary>
		public event EventHandler? Changed;

		public bool HasSelection => Options.Any(o => o.IsChecked);

		public List<string> SelectedNames =>
			Options.Where(o => o.IsChecked).Select(o => o.Name).ToList();

		public bool IsSelected(string name) =>
			Options.Any(o => o.IsChecked && o.Name == name);

		public FilterGroup(IEnumerable<string> names, IEnumerable<string>? initiallySelected = null)
		{
			var initial = new HashSet<string>(initiallySelected ?? Array.Empty<string>());

			foreach (var name in names)
			{
				var option = new FilterOption(name) { IsChecked = initial.Contains(name) };
				option.PropertyChanged += OnOptionChanged;
				Options.Add(option);
			}

			// Kezdőállapot: ha nincs kijelölt chip, az All be van kapcsolva
			_suppressSync = true;
			try { IsAll = !HasSelection; }
			finally { _suppressSync = false; }
		}

		partial void OnIsAllChanged(bool value)
		{
			if (_suppressSync) return; // programból állítottuk, nem a felhasználó

			_suppressSync = true;
			try
			{
				if (value)
				{
					_snapshot = Options.Select(o => o.IsChecked).ToList();
					foreach (var option in Options)
						option.IsChecked = false;
				}
				else
				{
					if (_snapshot != null)
					{
						for (int i = 0; i < Options.Count && i < _snapshot.Count; i++)
							Options[i].IsChecked = _snapshot[i];
					}
					_snapshot = null;
				}
			}
			finally
			{
				_suppressSync = false;
			}

			Changed?.Invoke(this, EventArgs.Empty);
		}

		private void OnOptionChanged(object? sender, PropertyChangedEventArgs e)
		{
			if (e.PropertyName != nameof(FilterOption.IsChecked)) return;
			if (_suppressSync) return;

			int checkedCount = Options.Count(o => o.IsChecked);

			if (checkedCount == Options.Count)
			{
				// Kézzel mindet bepipáltad = ugyanaz, mint az All: chipek kiürítése, All bekapcsol
				_suppressSync = true;
				try
				{
					foreach (var option in Options)
						option.IsChecked = false;
					IsAll = true;
				}
				finally { _suppressSync = false; }

				_snapshot = null;
			}
			else if (checkedCount == 0)
			{
				// Az utolsó chipet is kivetted: nincs szűrés, vagyis All
				SetIsAllSilently(true);
				_snapshot = null;
			}
			else if (IsAll)
			{
				// Chipre kattintottál, miközben az All be volt kapcsolva:
				// csak ez a chip marad a szűrő, az All kikapcsol
				SetIsAllSilently(false);
				_snapshot = null;
			}

			Changed?.Invoke(this, EventArgs.Empty);
		}

		private void SetIsAllSilently(bool value)
		{
			_suppressSync = true;
			try { IsAll = value; }
			finally { _suppressSync = false; }
		}
	}
}