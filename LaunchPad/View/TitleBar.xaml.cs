using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace LaunchPad.View
{
    public partial class TitleBar : UserControl 
    {
        public TitleBar()
        {
            InitializeComponent();
        }

        private Point mouseDownPos;
        private bool leftmousebtndonw = false;

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTCAPTION = 0x02;

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount > 1) return;

            var window = Window.GetWindow(this);
            if (window == null) return;

            if (window.WindowState == WindowState.Maximized)
            {
                mouseDownPos = e.GetPosition(window);
                leftmousebtndonw = true;
                Mouse.Capture(this, CaptureMode.SubTree);
            }
            else
            {
                window.DragMove();
            }
        }

        private void Window_MouseMove(object sender, MouseEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;

            if (!leftmousebtndonw || window.WindowState != WindowState.Maximized) return;

            leftmousebtndonw = false;
            if (Mouse.Captured == this) Mouse.Capture(null);

            ReleaseCapture();
            var hwnd = new WindowInteropHelper(window).Handle;
            SendMessage(hwnd, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);

        }

        private void Window_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            leftmousebtndonw = false;
            if (Mouse.Captured == this) Mouse.Capture(null);
        }

        private void TitleBar_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;

            // Toggle maximize / restore on double-click
            if (window.WindowState == WindowState.Normal)
            {
                window.WindowState = WindowState.Maximized;
            }
            else
            {
                window.WindowState = WindowState.Normal;
            }
        }
    }
}
