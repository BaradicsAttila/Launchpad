using CommunityToolkit.Mvvm.ComponentModel;

namespace LaunchPad.ViewModel
{
	public partial class FilterOption : ObservableObject
	{
		public string Name { get; }

		[ObservableProperty]
		private bool isChecked;

		public FilterOption(string name) => Name = name;
	}
}