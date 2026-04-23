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

        [RelayCommand]
        private void maximbtn()
        {

        }

        [RelayCommand]
        private void minimbtn()
        {
            mainwindow?.WindowState = WindowState.Minimized;
        }



        public TiltlebarVM()
        {
            
            mainwindow = Application.Current?.MainWindow;
            if (mainwindow != null)
            {
                _windowSt = mainwindow.WindowState;
                mainwindow.StateChanged += Mainwindow_StateChanged;
            }
        }

        private void Mainwindow_StateChanged(object? sender, EventArgs e)
        {
            WindowSt = mainwindow?.WindowState ?? WindowState.Normal;
            if (mainwindow?.WindowState==WindowState.Maximized)
            {
                
            }
        }

        [ObservableProperty]
        private WindowState _windowSt;

        [ObservableProperty]
        private string _maximbtnContent;
    }
}
