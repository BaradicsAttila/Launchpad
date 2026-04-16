using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Launch_Pad.View
{
    /// <summary>
    /// Interaction logic for TitleBar.xaml
    /// </summary>
    /// 🗖
    public partial class TitleBar : UserControl
	{
		public TitleBar()
		{
			InitializeComponent();
		}


		private void clsbtn_Click(object sender, RoutedEventArgs e)
		{
			Environment.Exit(0);
		}

        private void maximbtn_Click(object sender, RoutedEventArgs e)
        {
            var mainwindow = Window.GetWindow(this);
            if (mainwindow.WindowState == WindowState.Maximized)
            {
                mainwindow.WindowState = WindowState.Normal;
            }
            else
            {
                mainwindow.WindowState = WindowState.Maximized;

            }
        }

        private void minimbtn_Click(object sender, RoutedEventArgs e)
        {

        }
    }
}
