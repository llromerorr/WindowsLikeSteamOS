using System;
using System.Windows;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            AppDomain.CurrentDomain.UnhandledException += (s, ev) => 
            {
                Logger.Log($"[FATAL CRASH] {ev.ExceptionObject}");
            };
            
            this.DispatcherUnhandledException += (s, ev) => 
            {
                try
                {
                    Logger.Log($"[WPF EXCEPTION] {ev.Exception}");
                    MessageBox.Show($"Error en el configurador de SteamOS:\n\n{ev.Exception.Message}", "SteamOS", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                catch { }
                ev.Handled = true;
            };

            try { NativeMethods.SetProcessDpiAwarenessContext(NativeMethods.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2); } catch { }
            base.OnStartup(e);

            try
            {
                Logger.Log("[SteamOS.Config] Iniciando proceso de configuración...");
                NativeMethods.SystemParametersInfoTimeout(NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE | NativeMethods.SPIF_UPDATEINIFILE);

                this.ShutdownMode = ShutdownMode.OnMainWindowClose;
                MainWindow main = new MainWindow();
                this.MainWindow = main;
                main.Show();
                Logger.Log("[SteamOS.Config] MainWindow mostrada correctamente.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[App.OnStartup] Error al iniciar ventana principal: {ex}");
                MessageBox.Show($"Error al iniciar la interfaz de configuración:\n{ex.Message}\n\n{ex.StackTrace}", "Error SteamOS", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
