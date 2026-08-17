using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SteamOSConfigurator.Helpers
{
    public static class IconHelper
    {
        public static void AsignarIcono(Window window, Image? targetImage = null)
        {
            try
            {
                BitmapSource? bitmapSource = ObtenerIconoAltaResolucion();

                if (bitmapSource != null)
                {
                    bitmapSource.Freeze();
                    window.Icon = bitmapSource;

                    if (targetImage != null)
                    {
                        RenderOptions.SetBitmapScalingMode(targetImage, BitmapScalingMode.HighQuality);
                        RenderOptions.SetEdgeMode(targetImage, EdgeMode.Unspecified);
                        targetImage.Source = bitmapSource;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[IconHelper] Error asignando icono: {ex.Message}");
            }
        }

        public static BitmapSource? ObtenerIconoAltaResolucion()
        {
            // 1. Intento por recurso incrustado en WPF (pack URI)
            try
            {
                var uri = new Uri("pack://application:,,,/icon.png", UriKind.RelativeOrAbsolute);
                var sri = Application.GetResourceStream(uri);
                if (sri != null)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.StreamSource = sri.Stream;
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    return bmp;
                }
            }
            catch { }

            // 2. Intento por Assembly GetManifestResourceStream (icon.png o icon.ico)
            try
            {
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                foreach (var resName in new[] { "icon.png", "icon.ico" })
                {
                    using (var stream = assembly.GetManifestResourceStream(resName))
                    {
                        if (stream != null)
                        {
                            var ms = new MemoryStream();
                            stream.CopyTo(ms);
                            ms.Position = 0;

                            if (resName.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                            {
                                var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                                if (decoder.Frames.Count > 0)
                                {
                                    // Seleccionar el frame de mayor resolución
                                    BitmapFrame bestFrame = decoder.Frames[0];
                                    foreach (var f in decoder.Frames)
                                    {
                                        if (f.PixelWidth > bestFrame.PixelWidth)
                                            bestFrame = f;
                                    }
                                    return bestFrame;
                                }
                            }
                            else
                            {
                                var bmp = new BitmapImage();
                                bmp.BeginInit();
                                bmp.StreamSource = ms;
                                bmp.CacheOption = BitmapCacheOption.OnLoad;
                                bmp.EndInit();
                                return bmp;
                            }
                        }
                    }
                }
            }
            catch { }

            // 3. Intento desde disco (icon.png o icon.ico en carpeta de la app o AppPaths)
            try
            {
                string dirActual = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string[] rutas = new[]
                {
                    Path.Combine(dirActual, "icon.png"),
                    Path.Combine(dirActual, "icon.ico"),
                    Path.Combine(AppPaths.RaizDatos, "icon.png"),
                    Path.Combine(AppPaths.RaizDatos, "icon.ico"),
                    Path.Combine(AppContext.BaseDirectory, "icon.png"),
                    Path.Combine(AppContext.BaseDirectory, "icon.ico")
                };

                foreach (var ruta in rutas)
                {
                    if (File.Exists(ruta))
                    {
                        byte[] bytes = File.ReadAllBytes(ruta);
                        var ms = new MemoryStream(bytes);

                        if (ruta.EndsWith(".ico", StringComparison.OrdinalIgnoreCase))
                        {
                            var decoder = new IconBitmapDecoder(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                            if (decoder.Frames.Count > 0)
                            {
                                BitmapFrame bestFrame = decoder.Frames[0];
                                foreach (var f in decoder.Frames)
                                {
                                    if (f.PixelWidth > bestFrame.PixelWidth)
                                        bestFrame = f;
                                }
                                return bestFrame;
                            }
                        }
                        else
                        {
                            var bmp = new BitmapImage();
                            bmp.BeginInit();
                            bmp.StreamSource = ms;
                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                            bmp.EndInit();
                            return bmp;
                        }
                    }
                }
            }
            catch { }

            return null;
        }
    }
}
