using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace IsoToUsb;

/// <summary>
/// Custom WinUI 3 entry point.
/// </summary>
/// <remarks>
/// We replace the XAML-generated <c>Main</c> (suppressed via the
/// <c>DISABLE_XAML_GENERATED_MAIN</c> compile constant) so we can:
/// <list type="number">
///   <item>Dispatch into the headless elevated <see cref="Worker.PipelineWorker"/>
///         when launched with <c>--worker</c>, before any WinUI initialization.</item>
///   <item>Set <c>MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY</c> before the
///         Windows App SDK runtime loads (required for single-file
///         self-contained publishes — the bootstrapper has to know where the
///         extracted runtime DLLs live).</item>
/// </list>
/// </remarks>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Worker mode: skip WinUI entirely, run the elevated pipeline, exit.
        // The non-elevated UI process launches us in this mode via
        // Services/ElevatedWorkerLauncher.cs.
        if (args.Length > 0 && Array.IndexOf(args, "--worker") >= 0)
        {
            try
            {
                return Worker.PipelineWorker.Run(args);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[IsoToUsb worker] fatal: {ex}");
                return 1;
            }
        }

        Environment.SetEnvironmentVariable(
            "MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY",
            AppContext.BaseDirectory);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
