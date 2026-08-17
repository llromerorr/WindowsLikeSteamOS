using System;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace SteamOSConfigurator.Helpers
{
    public static class IconHelper
    {
        public static void AsignarIcono(Window window, System.Windows.Controls.Image? targetImage = null)
        {
            try
            {
                BitmapSource? bitmapSource = null;

                // Intento 1: Extraer el icono directamente del proceso .exe en ejecución
                string exePath = Environment.ProcessPath ?? "";
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    using (var sysIcon = Icon.ExtractAssociatedIcon(exePath))
                    {
                        if (sysIcon != null)
                        {
                            bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                                sysIcon.Handle,
                                Int32Rect.Empty,
                                BitmapSizeOptions.FromEmptyOptions());
                        }
                    }
                }

                // Intento 2: Si no, buscar icon.ico en la carpeta del ejecutable o en C:\ProgramData\SteamOS
                if (bitmapSource == null)
                {
                    string[] posiblesRutas = new[]
                    {
                        Path.Combine(AppContext.BaseDirectory, "icon.ico"),
                        Path.Combine(AppPaths.RaizDatos, "icon.ico"),
                        "icon.ico"
                    };

                    foreach (var ruta in posiblesRutas)
                    {
                        if (File.Exists(ruta))
                        {
                            using (var sysIcon = new Icon(ruta))
                            {
                                bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                                    sysIcon.Handle,
                                    Int32Rect.Empty,
                                    BitmapSizeOptions.FromEmptyOptions());
                                break;
                            }
                        }
                    }
                }

                if (bitmapSource != null)
                {
                    bitmapSource.Freeze();
                    window.Icon = bitmapSource;
                    if (targetImage != null)
                    {
                        targetImage.Source = bitmapSource;
                    }
                }
            }
            catch { }
        }
    }
}
