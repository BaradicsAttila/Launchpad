using System;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Launch_Pad.ViewModel
{
    public partial class TiltlebarVM : ObservableObject
    {
        private Window? mainwindow;
        [RelayCommand]
        private void clsbtn()
        {
            Environment.Exit(0);
        }

		public TiltlebarVM()
        {
            
            mainwindow = Application.Current?.MainWindow;
            if (mainwindow != null)
            {
                // initialize backing field and keep in sync when the window state changes
                _windowSt = mainwindow.WindowState;
                mainwindow.StateChanged += Mainwindow_StateChanged;
            }
        }

        private void Mainwindow_StateChanged(object? sender, EventArgs e)
        {
            WindowSt = mainwindow?.WindowState ?? WindowState.Normal;
        }

        [ObservableProperty]
        private WindowState _windowSt;
    }
}
