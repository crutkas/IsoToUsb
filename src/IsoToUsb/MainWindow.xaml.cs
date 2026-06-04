using Microsoft.UI.Xaml;

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

        // Navigate the root frame to the main page on startup. Note: the UI
        // process runs at asInvoker (see app.manifest); elevation is only
        // raised when the user clicks Start, via ElevatedWorkerLauncher.
        RootFrame.Navigate(typeof(MainPage));
    }
}
