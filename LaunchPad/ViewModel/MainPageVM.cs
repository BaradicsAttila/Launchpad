using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using LaunchPad.View;

namespace LaunchPad.ViewModel
{
    public partial class MainPageVM : ObservableObject
    {
        [ObservableProperty]
        private UserControl _currentPage = new HomeView();
        [ObservableProperty]
        private bool _isHomeSelected = true;
        [ObservableProperty]
        private bool _isLibrarySelected;
        [ObservableProperty]
        private bool _isStatisticsSelected;
        [ObservableProperty]
        private bool _isMacrosSelected;
        [ObservableProperty]
        private bool _isSettingsSelected;

		partial void OnIsHomeSelectedChanged(bool value)
		{
			if (value)
			{
                CurrentPage = new HomeView();
			}
		}

		partial void OnIsLibrarySelectedChanged(bool value)
		{
			if (value)
			{
                CurrentPage = new Library();
			}
		}

		partial void OnIsStatisticsSelectedChanged(bool value)
		{
			if (value)
			{
				CurrentPage = new Statistics();
			}
		}
		partial void OnIsMacrosSelectedChanged(bool value)
		{
			if (value)
			{
				CurrentPage = new Macros();
			}
		}
		partial void OnIsSettingsSelectedChanged(bool value)
		{
			if (value)
			{
				CurrentPage = new Settings();
			}
		}
	}
}
