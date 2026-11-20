
using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LaunchPad.ViewModel
{
    public partial class TitlebarVM : ObservableObject
    {
        private Window? mainwindow;
        [RelayCommand]
        private void clsbtn()
        {
			mainwindow?.Close();
		}

        [RelayCommand]
        private void maximbtn()
        {
            if (mainwindow?.WindowState == WindowState.Maximized)
            {
                mainwindow.WindowState = WindowState.Normal;
            }
            else
            {
                mainwindow?.WindowState = WindowState.Maximized;
            }

        }

        [RelayCommand]
        private void minimbtn()
        {
            mainwindow?.WindowState = WindowState.Minimized;
        }

        private void UpdateMaximBtnContent()
        {
            if (mainwindow?.WindowState == WindowState.Maximized)
            {
                MaximbtnContent = "";
            }
            else
            {
                MaximbtnContent = "";
            }
        }

        public TitlebarVM()
        {
        }

        /// <summary>
        /// A TitleBar Loaded eseményéből hívjuk. Az Application.Current.MainWindow a VM
        /// létrehozásakor még nem feltétlenül a valódi főablak (a scan/setup ablakok előbb
        /// jönnek létre), ezért a tényleges ablakot a vizuális fából kérjük le.
        /// </summary>
        public void AttachWindow(Window? window)
        {
            if (window == null || ReferenceEquals(mainwindow, window)) return;

            if (mainwindow != null)
                mainwindow.StateChanged -= Mainwindow_StateChanged;

            mainwindow = window;
            WindowSt = window.WindowState;
            UpdateMaximBtnContent();
            window.StateChanged += Mainwindow_StateChanged;
        }

        private void Mainwindow_StateChanged(object? sender, EventArgs e)
        {
            WindowSt = mainwindow?.WindowState ?? WindowState.Normal;
            UpdateMaximBtnContent();
        }

        [ObservableProperty]
        private WindowState _windowSt;
        [ObservableProperty]
        private string _maximbtnContent = "";

	}
}

