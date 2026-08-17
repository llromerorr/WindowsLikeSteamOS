using System;
using System.Diagnostics;
using System.Security.Principal;
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
            
            if (!EsAdministrador()) 
            { 
                try 
                { 
                    using (var pStart = Process.Start(new ProcessStartInfo 
                    { 
                        UseShellExecute = true, 
                        WorkingDirectory = Environment.CurrentDirectory, 
                        FileName = Environment.ProcessPath, 
                        Arguments = e.Args.Length > 0 ? string.Join(" ", e.Args) : "", 
                        Verb = "runas" 
                    })) {} 
                } 
                catch { } 
                Environment.Exit(0);  
                return; 
            }

            NativeMethods.SystemParametersInfoTimeout(NativeMethods.SPI_SETFOREGROUNDLOCKTIMEOUT, 0, IntPtr.Zero, NativeMethods.SPIF_SENDCHANGE | NativeMethods.SPIF_UPDATEINIFILE);

            MainWindow main = new MainWindow();
            main.Show();
        }

        private bool EsAdministrador() 
        { 
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent()) 
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); 
        }
    }
}
