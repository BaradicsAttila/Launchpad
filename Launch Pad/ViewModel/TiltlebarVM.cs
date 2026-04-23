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

        public TiltlebarVM()
        {
            mainwindow = Application.Current?.MainWindow;
            if (mainwindow != null)
            {
                UpdateMaximBtnContent();   
                _windowSt = mainwindow.WindowState;
                mainwindow.StateChanged += Mainwindow_StateChanged;
            }
        }

        private void Mainwindow_StateChanged(object? sender, EventArgs e)
        {
            WindowSt = mainwindow?.WindowState ?? WindowState.Normal;
            UpdateMaximBtnContent();
        }

        [ObservableProperty]
        private WindowState _windowSt;
        [ObservableProperty]
        private string _maximbtnContent = string.Empty;

    }
}
