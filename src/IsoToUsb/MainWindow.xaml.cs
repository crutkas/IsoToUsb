using Microsoft.UI.Xaml;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace IsoToUsb;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Workshop three-pane layout: left inputs (280 DIP) · center log
        // (flex) · right phase list (300 DIP). The wider window gives the
        // center log column enough room to show full robocopy paths
        // without wrapping, while still feeling compact on a 1080p display.
        ResizeToDip(width: 1240, height: 760);

        // Navigate the root frame to the main page on startup. Note: the UI
        // process runs at asInvoker (see app.manifest); elevation is only
        // raised when the user clicks Start, via ElevatedWorkerLauncher.
        RootFrame.Navigate(typeof(MainPage));
    }

    private void ResizeToDip(int width, int height)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dpi = Windows.Win32.PInvoke.GetDpiForWindow(new Windows.Win32.Foundation.HWND(hwnd));
        if (dpi == 0)
        {
            dpi = 96;
        }
        var scale = dpi / 96.0;
        AppWindow.Resize(new SizeInt32(
            (int)System.Math.Round(width * scale),
            (int)System.Math.Round(height * scale)));
    }
}
