using System;
using System.IO;
using System.Windows;
using System.Runtime.InteropServices;
using System.Diagnostics;
using Microsoft.Win32;
using System.Threading.Tasks;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using AudioSwitcher.AudioApi.CoreAudio; 
using System.Security.Principal; 
using SteamOSConfigurator.Models;
using SteamOSConfigurator.Helpers;
using SteamOSConfigurator.Services;
using Wpf.Ui.Controls;
using Wpf.Ui.Appearance;

namespace SteamOSConfigurator
{
    public partial class MainWindow : FluentWindow
    {
        const int LOGON32_LOGON_INTERACTIVE = 2; const int LOGON32_PROVIDER_DEFAULT = 0;
        const int DISP_CHANGE_SUCCESSFUL = 0; const int DISP_CHANGE_BADMODE = -2; const int CDS_TEST = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PROFILEINFO { public int dwSize; public int dwFlags; public string lpUserName; public string lpProfilePath; public string lpDefaultPath; public string lpServerName; public string lpPolicyPath; public IntPtr hProfile; }
        
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct DEVMODE 
        { 
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; 
            public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra; 
            public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput; 
            public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate; 
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; 
            public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight; public int dmDisplayFlags; public int dmDisplayFrequency; 
            public int dmICMMethod; public int dmICMIntent; public int dmMediaType; public int dmDitherType; public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight; 
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);
        [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern bool LoadUserProfile(IntPtr hToken, ref PROFILEINFO lpProfileInfo);
        [DllImport("userenv.dll", SetLastError = true)] public static extern bool UnloadUserProfile(IntPtr hToken, IntPtr hProfile);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("userenv.dll", CharSet = CharSet.Auto, SetLastError = true)] public static extern bool DeleteProfile(string lpSidString, string? lpProfilePath, string? lpComputerName);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon, IntPtr[] phicon, int[] piconid, int nIcons, int flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
        
        const int ENUM_CURRENT_SETTINGS = -1; const int DM_PELSWIDTH = 0x00080000; const int DM_PELSHEIGHT = 0x00100000; const int DM_DISPLAYFREQUENCY = 0x00400000;
        private bool _entornoInstalado = false;
        private List<DEVMODE> _resolucionesSoportadas = new List<DEVMODE>();
        private List<(string DeviceName, bool IsPrimary)> _monitorInfo = new();

        public MainWindow() { InitializeComponent(); VerificarEstadoSistema(); CargarDatosIniciales(); }

        /// <summary>
        /// Enumera los monitores activos del sistema usando EnumDisplayDevices (P/Invoke).
        /// Reemplaza la dependencia de Screen.AllScreens (Windows Forms).
        /// </summary>
        private List<(string DeviceName, bool IsPrimary)> EnumerarMonitores()
        {
            var resultado = new List<(string DeviceName, bool IsPrimary)>();
            int id = 0;
            DisplayHelper.DISPLAY_DEVICE dd = new DisplayHelper.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayHelper.DISPLAY_DEVICE>() };
            while (true)
            {
                if (!EnumDisplayDevicesNative(null, id, ref dd, 0)) break;
                // StateFlags bit 0x1 = activo, bit 0x4 = primario
                if ((dd.StateFlags & 0x1) != 0)
                {
                    bool esPrimario = (dd.StateFlags & 0x4) != 0;
                    resultado.Add((dd.DeviceName, esPrimario));
                }
                id++;
                dd = new DisplayHelper.DISPLAY_DEVICE { cb = Marshal.SizeOf<DisplayHelper.DISPLAY_DEVICE>() };
            }
            return resultado;
        }

        [DllImport("user32.dll", CharSet = CharSet.Auto, EntryPoint = "EnumDisplayDevices")]
        private static extern bool EnumDisplayDevicesNative(string? lpDevice, int iDevNum, ref DisplayHelper.DISPLAY_DEVICE lpDisplayDevice, int dwFlags);

        private void ActualizarUIEstado()
        {
            var depService = new Services.DependencyService();
            
            // 1. Steam
            if (depService.SteamInstalado)
            {
                dotSteam.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#10D275")!; // Verde
                lblSimpleSteam.Text = "Steam: Instalado y detectado";
                lblEstadoSteam.Text = "Detectado";
                lblEstadoSteam.Foreground = System.Windows.Media.Brushes.SpringGreen;
            }
            else
            {
                dotSteam.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#FF4A4A")!; // Rojo
                lblSimpleSteam.Text = "Steam: No instalado (Se auto-descargará)";
                lblEstadoSteam.Text = "No detectado";
                lblEstadoSteam.Foreground = System.Windows.Media.Brushes.Crimson;
            }
            
            // 2. Drivers
            bool driversOk = depService.ViGEmInstalado && depService.HidHideInstalado;
            if (driversOk)
            {
                dotDrivers.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#10D275")!; // Verde
                lblSimpleDrivers.Text = "Controladores Mando (ViGEm + HidHide): Instalados";
                lblEstadoDrivers.Text = "OK (ViGEm + HidHide)";
                lblEstadoDrivers.Foreground = System.Windows.Media.Brushes.SpringGreen;
            }
            else
            {
                dotDrivers.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#F5A623")!; // Naranja
                lblSimpleDrivers.Text = "Controladores Mando: Faltan Drivers (ViGEm/HidHide)";
                lblEstadoDrivers.Text = "Faltan drivers";
                lblEstadoDrivers.Foreground = System.Windows.Media.Brushes.Orange;
            }
            
            // 3. Mando / Mapeo
            bool mandoMapeado = false;
            if (File.Exists(AppPaths.MapeoConfig))
            {
                try
                {
                    var mapeo = JsonSerializer.Deserialize<MapeoControl>(File.ReadAllText(AppPaths.MapeoConfig));
                    if (mapeo != null && !string.IsNullOrEmpty(mapeo.NombreControl))
                    {
                        mandoMapeado = true;
                        lblSimpleMando.Text = $"Mando: {mapeo.NombreControl}";
                    }
                }
                catch { }
            }
            
            if (mandoMapeado)
            {
                dotMando.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#10D275")!; // Verde
            }
            else
            {
                dotMando.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#F5A623")!; // Naranja
                lblSimpleMando.Text = "Mando: Sin mapear";
            }
            
            // 4. RivaTuner (RTSS)
            if (depService.RtssInstalado)
            {
                dotRTSS.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#10D275")!; // Verde
                lblSimpleRTSS.Text = "RivaTuner (RTSS): Instalado y listo";
                lblEstadoRTSS.Text = "Preparado";
                lblEstadoRTSS.Foreground = System.Windows.Media.Brushes.SpringGreen;
            }
            else
            {
                dotRTSS.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#FF4A4A")!; // Rojo
                lblSimpleRTSS.Text = "RivaTuner (RTSS): No instalado (Se auto-descargará)";
                lblEstadoRTSS.Text = "No detectado";
                lblEstadoRTSS.Foreground = System.Windows.Media.Brushes.Crimson;
            }

            // 4.5 MSI Afterburner
            if (depService.AfterburnerInstalado)
            {
                dotMSI.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#10D275")!; // Verde
                lblSimpleMSI.Text = "MSI Afterburner: Instalado y listo";
                lblEstadoMSI.Text = "Preparado";
                lblEstadoMSI.Foreground = System.Windows.Media.Brushes.SpringGreen;
            }
            else
            {
                dotMSI.Fill = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#FF4A4A")!; // Rojo
                lblSimpleMSI.Text = "MSI Afterburner: No instalado (Requerido)";
                lblEstadoMSI.Text = "No detectado";
                lblEstadoMSI.Foreground = System.Windows.Media.Brushes.Crimson;
            }
            
            // 5. Botón Acción Principal
            // El Content ahora se asigna en VerificarEstadoSistema
            
            btnDesinstalarSimple.Visibility = _entornoInstalado ? Visibility.Visible : Visibility.Collapsed;
        }

        private bool _requiereActualizacion = false;

        private void VerificarEstadoSistema()
        {
            try 
            { 
                var cuenta = new NTAccount("SteamOS"); 
                cuenta.Translate(typeof(SecurityIdentifier)); 
                _entornoInstalado = true; 
            }
            catch 
            { 
                _entornoInstalado = false; 
            }

            _requiereActualizacion = false;
            if (_entornoInstalado && File.Exists(AppPaths.EjecutableDestino))
            {
                try
                {
                    string rutaActual = Environment.ProcessPath ?? "";
                    if (!string.IsNullOrEmpty(rutaActual) && File.Exists(rutaActual) && !rutaActual.Equals(AppPaths.EjecutableDestino, StringComparison.OrdinalIgnoreCase))
                    {
                        var infoActual = new FileInfo(rutaActual);
                        var infoDestino = new FileInfo(AppPaths.EjecutableDestino);
                        // Si el que estamos corriendo es más nuevo, es una actualización
                        if (infoActual.LastWriteTime > infoDestino.LastWriteTime.AddMinutes(1))
                        {
                            _requiereActualizacion = true;
                        }
                    }
                }
                catch { }
            }

            if (!_entornoInstalado)
            {
                btnInstalar.Content = "INSTALAR STEAMOS";
                btnAccionPrincipal.Content = "INSTALAR STEAMOS";
            }
            else if (_requiereActualizacion)
            {
                btnInstalar.Content = "ACTUALIZAR STEAMOS";
                btnAccionPrincipal.Content = "ACTUALIZAR STEAMOS";
            }
            else
            {
                btnInstalar.Content = "APLICAR Y DEPLOYAR";
                btnAccionPrincipal.Content = "APLICAR CONFIGURACIÓN";
            }
            
            ActualizarUIEstado();
        }

        private void CargarDatosIniciales()
        {
            _monitorInfo = EnumerarMonitores();
            cmbMonitores.Items.Clear(); foreach (var monitor in _monitorInfo) cmbMonitores.Items.Add($"{monitor.DeviceName} ({(monitor.IsPrimary ? "Principal" : "Secundario")})");
            if (cmbMonitores.Items.Count > 0) cmbMonitores.SelectedIndex = 0;
            
            cmbAudio.Items.Clear();
            try { CoreAudioController controller = new CoreAudioController(); foreach (var device in controller.GetPlaybackDevices()) cmbAudio.Items.Add(device.FullName); } catch { }
            if (cmbAudio.Items.Count == 0) cmbAudio.Items.Add("Salida de audio por defecto"); cmbAudio.SelectedIndex = 0;
            
            VerificarDependenciasExtra();
            CargarConfiguracionGuardada();
        }

        private void VerificarDependenciasExtra()
        {
            ActualizarUIEstado();
        }

        private void CargarConfiguracionGuardada()
        {
            if (File.Exists(AppPaths.Config))
            {
                try
                {
                    var config = JsonSerializer.Deserialize<ConfiguracionSteamOS>(File.ReadAllText(AppPaths.Config));
                    if (config != null)
                    {
                        for (int i = 0; i < cmbMonitores.Items.Count; i++) 
                        {
                            string idFisico = DisplayHelper.ObtenerDeviceIdFisico(_monitorInfo[i].DeviceName);
                            if (!string.IsNullOrEmpty(config.MonitorDeviceId) && idFisico == config.MonitorDeviceId) { cmbMonitores.SelectedIndex = i; break; }
                            else if (!string.IsNullOrEmpty(config.MonitorDeviceName) && cmbMonitores.Items[i].ToString()!.Contains(config.MonitorDeviceName)) { cmbMonitores.SelectedIndex = i; break; }
                        }
                        
                        string resText = $"{config.ResolucionWidth} x {config.ResolucionHeight}";
                        for (int i = 0; i < cmbResoluciones.Items.Count; i++)
                        {
                            if (cmbResoluciones.Items[i].ToString() == resText) { cmbResoluciones.SelectedIndex = i; break; }
                        }
                        
                        string refText = $"{config.RefreshRate} Hz";
                        for (int i = 0; i < cmbRefresco.Items.Count; i++)
                        {
                            if (cmbRefresco.Items[i].ToString() == refText) { cmbRefresco.SelectedIndex = i; break; }
                        }
                        
                        for (int i = 0; i < cmbAudio.Items.Count; i++) if (cmbAudio.Items[i].ToString() == config.AudioDispositivo) { cmbAudio.SelectedIndex = i; break; }
                        
                        chkEmulador.IsChecked = config.EmuladorActivado;
                        
                        // Cargar Rendimiento y Latencia
                        cmbFPS.Text = config.LimiteFPS.ToString();
                        chkFastSync.IsChecked = config.ForzarFastSync;
                        txtDelayHome.Text = config.DelayBotonHome.ToString();
                    }
                }
                catch (Exception ex) { Logger.Log($"Error al cargar configuración guardada: {ex.Message}"); }
            }
            ActualizarNombreMando();
        }

        private void ActualizarNombreMando()
        {
            if (File.Exists(AppPaths.MapeoConfig))
            {
                try
                {
                    var mapeo = JsonSerializer.Deserialize<MapeoControl>(File.ReadAllText(AppPaths.MapeoConfig));
                    if (mapeo != null && !string.IsNullOrEmpty(mapeo.NombreControl))
                    {
                        lblNombreMando.Text = mapeo.NombreControl;
                        btnConfigurarMando.Content = "REMAPEAR";
                        lblNombreMando.Foreground = System.Windows.Media.Brushes.SpringGreen;
                        return;
                    }
                }
                catch (Exception ex) { Logger.Log($"Error al leer configuración de mapeo: {ex.Message}"); }
            }
            lblNombreMando.Text = "Ningún mando mapeado";
            btnConfigurarMando.Content = "MAPEAR MANDO";
            lblNombreMando.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#8A95A5")!;
        }

        private void BtnConfigurarMando_Click(object sender, RoutedEventArgs e) { VentanaMapeo ventana = new VentanaMapeo { Owner = this }; ventana.ShowDialog(); ActualizarNombreMando(); }

        private void CmbMonitores_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbMonitores.SelectedIndex < 0) return; 
            cmbResoluciones.Items.Clear(); 
            _resolucionesSoportadas.Clear();
            
            string deviceName = _monitorInfo[cmbMonitores.SelectedIndex].DeviceName; 
            DEVMODE devMode = new DEVMODE(); 
            devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); 
            int modeNum = 0;
            
            // Probar primero con el nombre de dispositivo específico
            while (EnumDisplaySettings(deviceName, modeNum, ref devMode) != 0) 
            { 
                if (devMode.dmBitsPerPel == 32 || devMode.dmBitsPerPel == 0) 
                    _resolucionesSoportadas.Add(devMode); 
                modeNum++; 
            }
            
            // Fallback si no retornó modos para deviceName específico
            if (_resolucionesSoportadas.Count == 0)
            {
                modeNum = 0;
                while (EnumDisplaySettings(null, modeNum, ref devMode) != 0)
                {
                    if (devMode.dmBitsPerPel == 32 || devMode.dmBitsPerPel == 0)
                        _resolucionesSoportadas.Add(devMode);
                    modeNum++;
                }
            }

            var resUnicas = _resolucionesSoportadas
                .GroupBy(d => new { d.dmPelsWidth, d.dmPelsHeight })
                .Where(g => g.Key.dmPelsWidth > 0 && g.Key.dmPelsHeight > 0)
                .OrderByDescending(g => g.Key.dmPelsWidth * g.Key.dmPelsHeight)
                .ToList();

            foreach(var g in resUnicas) 
                cmbResoluciones.Items.Add($"{g.Key.dmPelsWidth} x {g.Key.dmPelsHeight}"); 

            // Fallback por defecto si aún no hay resoluciones
            if (cmbResoluciones.Items.Count == 0)
            {
                cmbResoluciones.Items.Add("3840 x 2160");
                cmbResoluciones.Items.Add("2560 x 1440");
                cmbResoluciones.Items.Add("1920 x 1080");
                cmbResoluciones.Items.Add("1600 x 900");
                cmbResoluciones.Items.Add("1366 x 768");
                cmbResoluciones.Items.Add("1280 x 720");
            }

            cmbResoluciones.SelectedIndex = 0; 
        }

        private void CmbResoluciones_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbResoluciones.SelectedIndex < 0 || cmbResoluciones.SelectedItem == null) return; 
            cmbRefresco.Items.Clear();
            
            string selText = cmbResoluciones.SelectedItem.ToString()!;
            if (selText.Contains("x"))
            {
                string[] partes = selText.Split('x'); 
                int w = int.Parse(partes[0].Trim()); 
                int h = int.Parse(partes[1].Trim());
                var hzUnicos = _resolucionesSoportadas
                    .Where(d => d.dmPelsWidth == w && d.dmPelsHeight == h && d.dmDisplayFrequency > 0)
                    .Select(d => d.dmDisplayFrequency)
                    .Distinct()
                    .OrderByDescending(hz => hz)
                    .ToList();
                    
                foreach(var hz in hzUnicos) cmbRefresco.Items.Add($"{hz} Hz"); 
            }

            if (cmbRefresco.Items.Count == 0)
            {
                cmbRefresco.Items.Add("60 Hz");
                cmbRefresco.Items.Add("120 Hz");
                cmbRefresco.Items.Add("144 Hz");
                cmbRefresco.Items.Add("165 Hz");
                cmbRefresco.Items.Add("240 Hz");
                cmbRefresco.Items.Add("59 Hz");
            }

            cmbRefresco.SelectedIndex = 0;
        }

        private bool VerificarSteamInstalado()
        {
            try { using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(AppPaths.SteamRegistryKey)) { if (key != null) { string? path = key.GetValue("InstallPath") as string; if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "steam.exe"))) return true; } } } catch { } return File.Exists(AppPaths.SteamFallback);
        }

        private void BtnVerAvanzado_Click(object sender, RoutedEventArgs e)
        {
            viewSimple.Visibility = Visibility.Collapsed;
            viewAvanzada.Visibility = Visibility.Visible;
        }

        private void BtnVolverSimple_Click(object sender, RoutedEventArgs e)
        {
            viewAvanzada.Visibility = Visibility.Collapsed;
            viewSimple.Visibility = Visibility.Visible;
        }

        private void DeshabilitarControlesInteraccion(string mensajeProgreso)
        {
            btnAccionPrincipal.IsEnabled = false;
            btnDesinstalarSimple.IsEnabled = false;
            btnVerAvanzado.IsEnabled = false;
            btnVolverSimple.IsEnabled = false;
            btnInstalar.IsEnabled = false;
            btnDesinstalar.IsEnabled = false;
            btnConfigurarMando.IsEnabled = false;

            panelProgreso.Visibility = Visibility.Visible;
            lblProgreso.Text = mensajeProgreso;
        }

        private void HabilitarControlesInteraccion()
        {
            panelProgreso.Visibility = Visibility.Collapsed;
            btnAccionPrincipal.IsEnabled = true;
            btnDesinstalarSimple.IsEnabled = true;
            btnVerAvanzado.IsEnabled = true;
            btnVolverSimple.IsEnabled = true;
            btnInstalar.IsEnabled = true;
            btnDesinstalar.IsEnabled = true;
            btnConfigurarMando.IsEnabled = true;
            VerificarEstadoSistema();
        }

        private async void BtnAccionPrincipal_Click(object sender, RoutedEventArgs e)
        {
            var depService = new Services.DependencyService();
            DeshabilitarControlesInteraccion("Comprobando e instalando componentes...");
            
            try
            {
                if (!depService.AfterburnerInstalado)
                {
                    HabilitarControlesInteraccion();
                    System.Windows.MessageBox.Show("MSI Afterburner es requerido para la telemetría (Sensores). Por favor, instálelo manualmente y vuelva a intentarlo.", "Error de Dependencia", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }

                if (!depService.SteamInstalado || !depService.RtssInstalado)
                {
                    if (!depService.SteamInstalado)
                    {
                        bool ok = await depService.InstalarSteamAsync(msg => 
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() => lblProgreso.Text = msg);
                        });
                        if (!ok)
                        {
                            System.Windows.MessageBox.Show("No se pudo descargar o instalar Steam de forma automática. Por favor, instálalo manualmente.", "Error de Dependencia", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                            HabilitarControlesInteraccion();
                            return;
                        }
                    }
                    
                    if (!depService.RtssInstalado)
                    {
                        bool ok = await depService.InstalarRtssAsync(msg => 
                        {
                            System.Windows.Application.Current.Dispatcher.Invoke(() => lblProgreso.Text = msg);
                        });
                        if (!ok)
                        {
                            System.Windows.MessageBox.Show("No se pudo descargar o instalar RivaTuner Statistics Server. Por favor, instálalo manualmente.", "Error de Dependencia", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                        }
                    }
                }
                
                await EjecutarInstalacionConfiguracion();
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"Ocurrió un error durante la instalación: {ex.Message}", "Error", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
            finally
            {
                HabilitarControlesInteraccion();
            }
        }

        private void BtnDesinstalarSimple_Click(object sender, RoutedEventArgs e)
        {
            BtnDesinstalar_Click(sender, e);
        }

        private async Task EjecutarInstalacionConfiguracion()
        {
            int indiceMonitor = cmbMonitores.SelectedIndex; 
            if (indiceMonitor < 0) indiceMonitor = 0;
            string resolucionTexto = cmbResoluciones.Text; 
            string refrescoTexto = cmbRefresco.Text; 
            string audioTexto = cmbAudio.Text;
            bool emuladorActivado = chkEmulador.IsChecked ?? false;

            int w = 1920, h = 1080, hz = 60, fps = 30, delay = 65;
            bool fastSync = chkFastSync.IsChecked ?? true;
            try
            {
                if (string.IsNullOrEmpty(resolucionTexto)) resolucionTexto = "1920 x 1080";
                if (string.IsNullOrEmpty(refrescoTexto)) refrescoTexto = "60 Hz";
                if (string.IsNullOrEmpty(cmbFPS.Text)) cmbFPS.Text = "60";
                if (string.IsNullOrEmpty(txtDelayHome.Text)) txtDelayHome.Text = "65";

                string[] partesRes = resolucionTexto.Split('x');
                w = int.Parse(partesRes[0].Trim()); 
                h = int.Parse(partesRes[1].Trim());
                hz = int.Parse(refrescoTexto.Replace("Hz", "").Trim());
                fps = int.Parse(cmbFPS.Text.Trim());
                delay = int.Parse(txtDelayHome.Text.Trim());
            } 
            catch (Exception ex) 
            { 
                System.Windows.MessageBox.Show($"Revisa los valores de configuración:\n{ex.Message}", "Error de Validación", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                throw;
            }

            try
            {
                string nombreUsuario = "SteamOS"; string passwordTemporal = "SteamOS123!"; 

                await Task.Run(() =>
                {
                    string rutaSeguraExe = InstalarEjecutableEnRutaSegura();
                    CrearAccesoDirectoConfiguracion();

                    if (!_entornoInstalado)
                    {
                        OptimizarInicioNuevoUsuario();
                        CrearUsuarioSteam(nombreUsuario, passwordTemporal);
                        ConstruirPerfilEnSegundoPlano(nombreUsuario, passwordTemporal, rutaSeguraExe);
                        
                        string sid = ObtenerSidUsuario(nombreUsuario);
                        if (!string.IsNullOrEmpty(sid)) ConfigurarIconoSteamOS(sid); 
                    }
                    
                    // Configurar AutoAdminLogon siempre, incluso si es solo actualizar/aplicar
                    bool autoLogon = false;
                    Application.Current.Dispatcher.Invoke(() => autoLogon = chkAutoLogon.IsChecked ?? false);
                    
                    try
                    {
                        // Asegurarnos de borrar la contraseña para que el usuario no tenga que poner clave al cambiar de cuenta
                        EjecutarComandoOculto($"net user {nombreUsuario} \"\"");

                        using (RegistryKey pwlessKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device"))
                        {
                            pwlessKey?.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
                        }

                        using (RegistryKey key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                        {
                            if (autoLogon)
                            {
                                key.SetValue("AutoAdminLogon", "1", RegistryValueKind.String);
                                key.SetValue("DefaultUserName", nombreUsuario, RegistryValueKind.String);
                                key.SetValue("DefaultDomainName", Environment.MachineName, RegistryValueKind.String);
                                key.SetValue("DefaultPassword", "", RegistryValueKind.String);
                            }
                            else
                            {
                                key.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
                                key.DeleteValue("DefaultUserName", false);
                                key.DeleteValue("DefaultDomainName", false);
                                key.DeleteValue("DefaultPassword", false);
                            }
                        }
                    }
                    catch { }
                });

                GuardarConfiguracionJson(indiceMonitor, w, h, hz, audioTexto, emuladorActivado, fps, fastSync, delay);
                System.Windows.MessageBox.Show(_entornoInstalado ? "Configuración de juego actualizada con éxito." : "¡Entorno Gaming creado con éxito!\n\nTu cuenta SteamOS está lista.", "Éxito", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            }
            catch (Exception ex) 
            { 
                System.Windows.MessageBox.Show($"Error en despliegue:\n{ex.Message}", "Error Crítico", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error); 
                throw;
            }
        }

        private async void BtnInstalar_Click(object sender, RoutedEventArgs e)
        {
            DeshabilitarControlesInteraccion("Instalando o aplicando configuración...");
            try
            {
                var depService = new Services.DependencyService();
                if (!depService.AfterburnerInstalado)
                {
                    HabilitarControlesInteraccion();
                    System.Windows.MessageBox.Show("MSI Afterburner es requerido para la telemetría (Sensores). Por favor, instálelo manualmente y vuelva a intentarlo.", "Error de Dependencia", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                    return;
                }
                
                await EjecutarInstalacionConfiguracion();
            }
            catch { }
            finally
            {
                HabilitarControlesInteraccion();
            }
        }

        private async void BtnDesinstalar_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.MessageBox.Show("¿Eliminar el entorno de consola y purgar la cuenta SteamOS?", "Desinstalar SteamOS", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning) == System.Windows.MessageBoxResult.Yes)
            {
                DeshabilitarControlesInteraccion("Eliminando cuenta SteamOS y desinstalando entorno... Por favor espera.");
                try 
                { 
                    await InstallationService.DesinstalarEntornoAsync(); 
                    System.Windows.MessageBox.Show("Entorno desinstalado con éxito del sistema.", "Desinstalación Completada", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information); 
                }
                catch (Exception ex) 
                { 
                    System.Windows.MessageBox.Show($"Error de purga:\n{ex.Message}", "Aviso", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning); 
                }
                finally 
                { 
                    HabilitarControlesInteraccion(); 
                }
            }
        }

        private void GuardarConfiguracionJson(int indiceMonitor, int w, int h, int hz, string audioPreferido, bool emuActivado, int fps, bool fastSync, int delay)
        {
            string deviceName = _monitorInfo[indiceMonitor].DeviceName;
            string deviceId = DisplayHelper.ObtenerDeviceIdFisico(deviceName);

            var config = ConfigManager.CargarConfiguracion();
            config.MonitorDeviceName = deviceName;
            config.MonitorDeviceId = deviceId;
            config.ResolucionWidth = w;
            config.ResolucionHeight = h;
            config.RefreshRate = hz;
            config.AudioDispositivo = audioPreferido;
            config.EmuladorActivado = emuActivado;
            config.LimiteFPS = fps;
            config.ForzarFastSync = fastSync;
            config.DelayBotonHome = delay;
            
            ConfigManager.GuardarConfiguracion(config);
        }

        // --- Funciones auxiliares de Windows ---
        private void CrearAccesoDirectoConfiguracion()
        {
            try
            {
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                string shortcutLocation = Path.Combine(desktopPath, "SteamOS.lnk");
                string oldShortcutLocation = Path.Combine(desktopPath, "Configurar WindowsLikeSteamOS.lnk");
                if (File.Exists(oldShortcutLocation)) { try { File.Delete(oldShortcutLocation); } catch { } }

                string targetPath = AppPaths.EjecutableDestino;
                string iconPath = Path.Combine(AppPaths.RaizDatos, "icon.ico");
                if (!File.Exists(iconPath) && File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico")))
                {
                    try { File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "icon.ico"), iconPath, true); } catch { }
                }

                string iconScript = File.Exists(iconPath) ? $"$Shortcut.IconLocation = '{iconPath}';" : "";
                string script = $"$WshShell = New-Object -comObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('{shortcutLocation}'); $Shortcut.TargetPath = '{targetPath}'; {iconScript} $Shortcut.Save()";
                EjecutarComandoOculto($"powershell -Command \"{script}\"");
            }
            catch { }
        }

        private void CrearUsuarioSteam(string nombreUsuario, string passwordTemporal) { EjecutarComandoOculto($"net user {nombreUsuario} {passwordTemporal} /add /y"); EjecutarComandoOculto($"wmic useraccount where \"name='{nombreUsuario}'\" set PasswordExpires=FALSE"); EjecutarComandoOculto($"net localgroup Administradores {nombreUsuario} /add"); EjecutarComandoOculto($"net localgroup Administrators {nombreUsuario} /add"); try { using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList")) { key?.SetValue(nombreUsuario, 1, RegistryValueKind.DWord); } } catch { } }
        private void EliminarUsuarioSteamOS() { string sid = ObtenerSidUsuario("SteamOS"); if (!string.IsNullOrEmpty(sid)) { /* DeleteProfile(sid, null, null); */ } EjecutarComandoOculto("net user SteamOS /delete"); /* try { Directory.Delete(@"C:\Users\SteamOS", true); } catch { } */ }
        private void EjecutarComandoOculto(string comando) { try { ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", $"/c {comando}") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false }; Process.Start(psi)?.WaitForExit(); } catch { } }
        private string ObtenerSidUsuario(string nombreUsuario) { try { var cuenta = new NTAccount(nombreUsuario); var sid = (SecurityIdentifier)cuenta.Translate(typeof(SecurityIdentifier)); return sid.Value; } catch { return ""; } }
        private void ConfigurarIconoSteamOS(string sidUsuario) { try { string rutaSteam = AppPaths.SteamFallback; using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(AppPaths.SteamRegistryKey)) { if (key != null) rutaSteam = Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe"); } if (File.Exists(rutaSteam)) { string rutaAvatar = AppPaths.Avatar; IntPtr[] phicon = new IntPtr[1]; int[] piconid = new int[1]; int result = PrivateExtractIcons(rutaSteam, 0, 256, 256, phicon, piconid, 1, 0); if (result > 0 && phicon[0] != IntPtr.Zero) { using (System.Drawing.Icon icon = System.Drawing.Icon.FromHandle(phicon[0])) using (System.Drawing.Bitmap bitmap = icon.ToBitmap()) { bitmap.Save(rutaAvatar, System.Drawing.Imaging.ImageFormat.Png); } DestroyIcon(phicon[0]); } using (RegistryKey key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sidUsuario}")) { if (key != null) { foreach(string size in new[] { "32", "40", "48", "96", "192", "200", "240", "448" }) key.SetValue($"Image{size}", rutaAvatar, RegistryValueKind.String); } } } } catch { } }
        private void OptimizarInicioNuevoUsuario() { try { using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System")) key?.SetValue("EnableFirstLogonAnimation", 0, RegistryValueKind.DWord); using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\OOBE")) key?.SetValue("DisablePrivacyExperience", 1, RegistryValueKind.DWord); } catch { } }
        private string InstalarEjecutableEnRutaSegura() { 
            string rutaOrigen = Environment.ProcessPath ?? throw new Exception("Error ruta ejecutable."); 
            string carpetaDestino = AppPaths.RaizDatos; 
            string rutaDestino = AppPaths.EjecutableDestino; 
            if (!Directory.Exists(carpetaDestino)) Directory.CreateDirectory(carpetaDestino); 
            File.Copy(rutaOrigen, rutaDestino, true); 
            
            string iconOrigen = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(rutaOrigen) ?? "", "icon.ico");
            string iconDestino = System.IO.Path.Combine(carpetaDestino, "icon.ico");
            if (File.Exists(iconOrigen)) try { File.Copy(iconOrigen, iconDestino, true); } catch { }

            string oldExe = System.IO.Path.Combine(carpetaDestino, "WindowsLikeSteamOS.exe");
            if (File.Exists(oldExe)) try { File.Delete(oldExe); } catch { }

            string jsonOrigen = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(rutaOrigen) ?? "", "juegos_perfiles.json");
            string jsonDestino = System.IO.Path.Combine(carpetaDestino, "juegos_perfiles.json");
            if (File.Exists(jsonOrigen)) File.Copy(jsonOrigen, jsonDestino, true);
            
            return rutaDestino; 
        }
        private void ConstruirPerfilEnSegundoPlano(string usuario, string contrasena, string rutaEjecutable) { IntPtr token = IntPtr.Zero; try { if (!LogonUser(usuario, ".", contrasena, LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out token)) throw new Exception("Error token."); PROFILEINFO p = new PROFILEINFO { dwSize = Marshal.SizeOf(typeof(PROFILEINFO)), lpUserName = usuario }; if (LoadUserProfile(token, ref p)) { string sid = ObtenerSidUsuario(usuario); using (RegistryKey? key = Registry.Users.CreateSubKey($@"{sid}\Software\Microsoft\Windows NT\CurrentVersion\Winlogon")) { if (key != null) { key.SetValue("Shell", $"\"{rutaEjecutable}\" -shell", RegistryValueKind.String); key.Flush(); } } UnloadUserProfile(token, p.hProfile); } } finally { if (token != IntPtr.Zero) CloseHandle(token); } }
    }
}