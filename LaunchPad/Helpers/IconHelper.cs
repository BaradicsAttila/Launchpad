using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LaunchPad.Helpers
{
	public static class IconHelper
	{
		private static readonly Guid IID_ShellItemImageFactory =
			new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

		[ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
		 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
		private interface IShellItemImageFactory
		{
			[PreserveSig]
			int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct SIZE
		{
			public int cx, cy;
			public SIZE(int x, int y) { cx = x; cy = y; }
		}

		[Flags]
		private enum SIIGBF
		{
			ResizeToFit = 0x00,
			BiggerSizeOk = 0x01,
			IconOnly = 0x04,
		}

		[DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
		private static extern void SHCreateItemFromParsingName(
			string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory factory);

		[DllImport("gdi32.dll")]
		private static extern bool DeleteObject(IntPtr hObject);

		public static ImageSource? GetIcon(string? exePath, int size = 128)
		{
			if (string.IsNullOrEmpty(exePath)) return null;

			// Perjelek -> backslash, relatív részek feloldása
			try { exePath = Path.GetFullPath(exePath); }
			catch { return null; }

			if (!File.Exists(exePath)) return null;

			return GetShellIcon(exePath, size) ?? GetLegacyIcon(exePath);
		}

		private static ImageSource? GetShellIcon(string exePath, int size)
		{
			IntPtr hBitmap = IntPtr.Zero;
			try
			{
				var iid = IID_ShellItemImageFactory;
				SHCreateItemFromParsingName(exePath, IntPtr.Zero, ref iid, out var factory);

				int hr = factory.GetImage(new SIZE(size, size),
										  SIIGBF.IconOnly | SIIGBF.BiggerSizeOk,
										  out hBitmap);
				if (hr != 0 || hBitmap == IntPtr.Zero)
				{
					System.Diagnostics.Debug.WriteLine($"[IconHelper] GetImage hiba 0x{hr:X8}: {exePath}");
					return null;
				}

				var source = Imaging.CreateBitmapSourceFromHBitmap(
					hBitmap, IntPtr.Zero, Int32Rect.Empty,
					BitmapSizeOptions.FromEmptyOptions());
				source.Freeze();
				return source;
			}
			catch (Exception ex)
			{
				System.Diagnostics.Debug.WriteLine($"[IconHelper] Shell hiba: {exePath} | {ex.Message}");
				return null;
			}
			finally
			{
				if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);
			}
		}

		// Fallback: a régi 32x32-es módszer, még mindig jobb, mint a semmi
		private static ImageSource? GetLegacyIcon(string exePath)
		{
			try
			{
				using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exePath);
				if (icon == null) return null;

				var source = Imaging.CreateBitmapSourceFromHIcon(
					icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
				source.Freeze();
				return source;
			}
			catch { return null; }
		}
	}
}