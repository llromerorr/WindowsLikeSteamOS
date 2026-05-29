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
using System.Drawing; 
using System.Security.Principal; 

namespace SteamOSConfigurator
{
    // AGREGAMOS LA VARIABLE EmuladorActivado AL MODELO DE DATOS
    public class ConfiguracionLocal
    {
        public string? MonitorDeviceName { get; set; }
        public int ResolucionWidth { get; set; }
        public int ResolucionHeight { get; set; }
        public int RefreshRate { get; set; }
        public string? AudioDispositivo { get; set; }
        public bool EmuladorActivado { get; set; } = true; 
    }

    public partial class MainWindow : Window
    {
        const int LOGON32_LOGON_INTERACTIVE = 2; const int LOGON32_PROVIDER_DEFAULT = 0;

        const int DISP_CHANGE_SUCCESSFUL = 0;
        const int DISP_CHANGE_BADMODE = -2;
        const int CDS_TEST = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        public struct PROFILEINFO { public int dwSize; public int dwFlags; public string lpUserName; public string lpProfilePath; public string lpDefaultPath; public string lpServerName; public string lpPolicyPath; public IntPtr hProfile; }
        [StructLayout(LayoutKind.Sequential)]
        public struct DEVMODE { [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName; public short dmSpecVersion; public short dmDriverVersion; public short dmSize; public short dmDriverExtra; public int dmFields; public int dmPositionX; public int dmPositionY; public int dmDisplayOrientation; public int dmDisplayFixedOutput; public short dmColor; public short dmDuplex; public short dmYResolution; public short dmTTOption; public short dmCollate; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName; public short dmLogPixels; public int dmBitsPerPel; public int dmPelsWidth; public int dmPelsHeight; public int dmDisplayFlags; public int dmDisplayFrequency; public int dmICMMethod; public int dmICMIntent; public int dmMediaType; public int dmDitherType; public int dmReserved1; public int dmReserved2; public int dmPanningWidth; public int dmPanningHeight; }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);
        [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Auto)] public static extern bool LoadUserProfile(IntPtr hToken, ref PROFILEINFO lpProfileInfo);
        [DllImport("userenv.dll", SetLastError = true)] public static extern bool UnloadUserProfile(IntPtr hToken, IntPtr hProfile);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("userenv.dll", CharSet = CharSet.Auto, SetLastError = true)] public static extern bool DeleteProfile(string lpSidString, string? lpProfilePath, string? lpComputerName);
        [DllImport("user32.dll")] public static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);
        [DllImport("user32.dll", CharSet = CharSet.Auto)] public static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon, IntPtr[] phicon, int[] piconid, int nIcons, int flags);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern int ChangeDisplaySettingsEx(string? lpszDeviceName, ref DEVMODE lpDevMode, IntPtr hwnd, int dwflags, IntPtr lParam);
        
        const int ENUM_CURRENT_SETTINGS = -1;
        const int DM_PELSWIDTH = 0x00080000;
        const int DM_PELSHEIGHT = 0x00100000;
        const int DM_DISPLAYFREQUENCY = 0x00400000;
        private bool _entornoInstalado = false;
        private List<DEVMODE> _resolucionesSoportadas = new List<DEVMODE>();

        public MainWindow() { InitializeComponent(); VerificarEstadoSistema(); CargarDatosIniciales(); }

        private void VerificarEstadoSistema()
        {
            try { var cuenta = new NTAccount("SteamOS"); cuenta.Translate(typeof(SecurityIdentifier)); _entornoInstalado = true; }
            catch { _entornoInstalado = false; }
            btnInstalar.Content = _entornoInstalado ? "APLICAR CONFIGURACIÓN" : "INSTALAR ENTORNO";
        }

        private void CargarDatosIniciales()
        {
            cmbMonitores.Items.Clear(); foreach (Screen pantalla in Screen.AllScreens) cmbMonitores.Items.Add($"{pantalla.DeviceName} ({(pantalla.Primary ? "Principal" : "Secundario")})");
            if (cmbMonitores.Items.Count > 0) cmbMonitores.SelectedIndex = 0;
            
            cmbAudio.Items.Clear();
            try { CoreAudioController controller = new CoreAudioController(); foreach (var device in controller.GetPlaybackDevices()) cmbAudio.Items.Add(device.FullName); } catch { }
            if (cmbAudio.Items.Count == 0) cmbAudio.Items.Add("Salida de audio por defecto"); cmbAudio.SelectedIndex = 0;
            
            if (VerificarSteamInstalado()) { lblEstadoSteam.Text = "Listo"; lblEstadoSteam.Foreground = System.Windows.Media.Brushes.SpringGreen; btnInstalar.IsEnabled = true; }
            else { lblEstadoSteam.Text = "No detectado"; lblEstadoSteam.Foreground = System.Windows.Media.Brushes.Crimson; btnInstalar.IsEnabled = false; }

            VerificarDriversMando();
            CargarConfiguracionGuardada();
        }

        private void VerificarDriversMando()
        {
            bool vigem = File.Exists(@"C:\Windows\System32\drivers\ViGEmBus.sys");
            bool hidhide = File.Exists(@"C:\Windows\System32\drivers\HidHide.sys");

            if (vigem && hidhide)
            {
                lblEstadoDrivers.Text = "Instalados (ViGEm + HidHide)";
                lblEstadoDrivers.Foreground = System.Windows.Media.Brushes.SpringGreen;
                btnConfigurarMando.IsEnabled = true;
                chkEmulador.IsEnabled = true;
            }
            else
            {
                lblEstadoDrivers.Text = "No detectados";
                lblEstadoDrivers.Foreground = System.Windows.Media.Brushes.Crimson;
                btnConfigurarMando.IsEnabled = false;
                chkEmulador.IsEnabled = false;
                chkEmulador.IsChecked = false;
            }
        }

        private void CargarConfiguracionGuardada()
        {
            string rutaConfig = @"C:\ProgramData\SteamOS\config.json";
            if (File.Exists(rutaConfig))
            {
                try
                {
                    var config = JsonSerializer.Deserialize<ConfiguracionLocal>(File.ReadAllText(rutaConfig));
                    if (config != null)
                    {
                        for (int i = 0; i < cmbMonitores.Items.Count; i++) if (cmbMonitores.Items[i].ToString()!.Contains(config.MonitorDeviceName!)) { cmbMonitores.SelectedIndex = i; break; }
                        
                        // Setear resoluciones editables (incluso si no existen en la lista)
                        cmbResoluciones.Text = $"{config.ResolucionWidth} x {config.ResolucionHeight}";
                        cmbRefresco.Text = $"{config.RefreshRate} Hz";
                        
                        for (int i = 0; i < cmbAudio.Items.Count; i++) if (cmbAudio.Items[i].ToString() == config.AudioDispositivo) { cmbAudio.SelectedIndex = i; break; }
                        
                        chkEmulador.IsChecked = config.EmuladorActivado;
                    }
                }
                catch { }
            }

            ActualizarNombreMando();
        }

        private void ActualizarNombreMando()
        {
            string rutaMapeo = @"C:\ProgramData\SteamOS\mapeo_config.json";
            if (File.Exists(rutaMapeo))
            {
                try
                {
                    var mapeo = JsonSerializer.Deserialize<MapeoControl>(File.ReadAllText(rutaMapeo));
                    if (mapeo != null && !string.IsNullOrEmpty(mapeo.NombreControl))
                    {
                        lblNombreMando.Text = mapeo.NombreControl;
                        btnConfigurarMando.Content = "RECONFIGURAR";
                        lblNombreMando.Foreground = System.Windows.Media.Brushes.SpringGreen;
                        return;
                    }
                }
                catch { }
            }
            lblNombreMando.Text = "Ningún mando configurado";
            btnConfigurarMando.Content = "CONFIGURAR";
            lblNombreMando.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFrom("#707A8C")!;
        }

        private void BtnConfigurarMando_Click(object sender, RoutedEventArgs e)
        {
            VentanaMapeo ventana = new VentanaMapeo { Owner = this };
            ventana.ShowDialog();
            ActualizarNombreMando(); // Refresca el nombre tras cerrar la ventana
        }

        private void CmbMonitores_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbMonitores.SelectedIndex < 0) return; cmbResoluciones.Items.Clear(); _resolucionesSoportadas.Clear();
            string deviceName = Screen.AllScreens[cmbMonitores.SelectedIndex].DeviceName; DEVMODE devMode = new DEVMODE(); devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); int modeNum = 0;
            while (EnumDisplaySettings(deviceName, modeNum, ref devMode) != 0) { if (devMode.dmBitsPerPel == 32) _resolucionesSoportadas.Add(devMode); modeNum++; }
            var resUnicas = _resolucionesSoportadas.GroupBy(d => new { d.dmPelsWidth, d.dmPelsHeight }).OrderByDescending(g => g.Key.dmPelsWidth * g.Key.dmPelsHeight).ToList();
            foreach(var g in resUnicas) cmbResoluciones.Items.Add($"{g.Key.dmPelsWidth} x {g.Key.dmPelsHeight}"); if (cmbResoluciones.Items.Count > 0) cmbResoluciones.SelectedIndex = 0; 
        }

        private void CmbResoluciones_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (cmbResoluciones.SelectedIndex < 0 || cmbResoluciones.SelectedItem == null) return; cmbRefresco.Items.Clear();
            string[] partes = cmbResoluciones.SelectedItem.ToString()!.Split('x'); int w = int.Parse(partes[0].Trim()); int h = int.Parse(partes[1].Trim());
            var hzUnicos = _resolucionesSoportadas.Where(d => d.dmPelsWidth == w && d.dmPelsHeight == h).Select(d => d.dmDisplayFrequency).Distinct().OrderByDescending(hz => hz).ToList();
            foreach(var hz in hzUnicos) cmbRefresco.Items.Add($"{hz} Hz"); if (cmbRefresco.Items.Count > 0) cmbRefresco.SelectedIndex = 0;
        }

        private void BtnForzarResolucion_Click(object sender, RoutedEventArgs e)
        {
            if (cmbMonitores.SelectedIndex < 0) return;
            string deviceName = Screen.AllScreens[cmbMonitores.SelectedIndex].DeviceName;

            try
            {
                // Leemos lo que escribiste a mano en las cajas
                string[] partesRes = cmbResoluciones.Text.Split('x');
                int w = int.Parse(partesRes[0].Trim());
                int h = int.Parse(partesRes[1].Trim());
                int hz = int.Parse(cmbRefresco.Text.Replace("Hz", "").Trim());

                DEVMODE mode = new DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)) };
                EnumDisplaySettings(deviceName, ENUM_CURRENT_SETTINGS, ref mode);

                mode.dmPelsWidth = w;
                mode.dmPelsHeight = h;
                mode.dmDisplayFrequency = hz;
                mode.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;

                // FASE 1: El test de estrés a la tarjeta gráfica
                int testResult = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, CDS_TEST, IntPtr.Zero);

                if (testResult == DISP_CHANGE_SUCCESSFUL)
                {
                    // FASE 2: Si lo acepta, lo inyectamos al registro a la fuerza
                    int applyResult = ChangeDisplaySettingsEx(deviceName, ref mode, IntPtr.Zero, 0x00000001 /* CDS_UPDATEREGISTRY */, IntPtr.Zero);
                    
                    if (applyResult == DISP_CHANGE_SUCCESSFUL)
                    {
                         System.Windows.MessageBox.Show($"¡Inyección exitosa! La tarjeta gráfica aceptó forzar la señal a {w}x{h} @ {hz}Hz.", "Blind Output Exitoso", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                    else
                    {
                         System.Windows.MessageBox.Show($"La tarjeta pasó el test, pero Windows bloqueó la aplicación final. Código: {applyResult}", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                else if (testResult == DISP_CHANGE_BADMODE)
                {
                    System.Windows.MessageBox.Show($"❌ EL DRIVER RECHAZÓ LA SEÑAL (Código -2: BADMODE).\n\nEl televisor tiene el hardware bloqueado y la gráfica se niega a disparar a ciegas. Oficialmente necesitamos modificar el EDID (Mini-CRU) para engañarla.", "Hardware Bloqueado", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                else
                {
                    System.Windows.MessageBox.Show($"Fallo desconocido de la API. Código de error: {testResult}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            catch (Exception)
            {
                System.Windows.MessageBox.Show("Por favor, asegúrate de escribir el formato correcto.\nEjemplo: '3840 x 2160' en resolución y '60' en hercios.", "Error de Formato", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private bool VerificarSteamInstalado()
        {
            try { using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")) { if (key != null) { string? path = key.GetValue("InstallPath") as string; if (!string.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, "steam.exe"))) return true; } } } catch { } return File.Exists(@"C:\Program Files (x86)\Steam\steam.exe");
        }

        private async void BtnInstalar_Click(object sender, RoutedEventArgs e)
        {
            int indiceMonitor = cmbMonitores.SelectedIndex; 
            string resolucionTexto = cmbResoluciones.Text; // Lee el texto (aunque sea tipeado a mano)
            string refrescoTexto = cmbRefresco.Text; 
            string audioTexto = cmbAudio.Text;
            bool emuladorActivado = chkEmulador.IsChecked ?? false;

            btnInstalar.IsEnabled = false; btnDesinstalar.IsEnabled = false; btnInstalar.Content = _entornoInstalado ? "APLICANDO..." : "INSTALANDO...";

            try
            {
                string nombreUsuario = "SteamOS"; string passwordTemporal = "SteamOS123!"; 

                await Task.Run(() =>
                {
                    string rutaSeguraExe = InstalarEjecutableEnRutaSegura();

                    if (!_entornoInstalado)
                    {
                        OptimizarInicioNuevoUsuario();
                        CrearUsuarioSteam(nombreUsuario, passwordTemporal);
                        ConstruirPerfilEnSegundoPlano(nombreUsuario, passwordTemporal, rutaSeguraExe);
                        
                        string sid = ObtenerSidUsuario(nombreUsuario);
                        if (!string.IsNullOrEmpty(sid)) ConfigurarIconoSteamOS(sid); 
                        
                        EjecutarComandoOculto($"net user {nombreUsuario} \"\"");
                    }
                });

                GuardarConfiguracionJson(indiceMonitor, resolucionTexto, refrescoTexto, audioTexto, emuladorActivado);
                System.Windows.MessageBox.Show(_entornoInstalado ? "Configuración de juego actualizada con éxito." : "¡Entorno Gaming creado con éxito!\n\nTu cuenta SteamOS está lista y SIN contraseña.", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information);
                VerificarEstadoSistema(); 
            }
            catch (Exception ex) { System.Windows.MessageBox.Show($"Error en despliegue:\n{ex.Message}", "Error Crítico", MessageBoxButton.OK, MessageBoxImage.Error); }
            finally { btnInstalar.IsEnabled = true; btnInstalar.Content = _entornoInstalado ? "APLICAR CONFIGURACIÓN" : "INSTALAR ENTORNO"; btnDesinstalar.IsEnabled = true; }
        }

        private async void BtnDesinstalar_Click(object sender, RoutedEventArgs e)
        {
            if (System.Windows.MessageBox.Show("¿Eliminar el entorno de consola y purgar la cuenta?", "Desinstalar", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
            {
                btnDesinstalar.IsEnabled = false; btnDesinstalar.Content = "BORRANDO..."; btnInstalar.IsEnabled = false;
                try { await Task.Run(() => { EliminarUsuarioSteamOS(); }); System.Windows.MessageBox.Show("Entorno desinstalado del núcleo del sistema.", "Éxito", MessageBoxButton.OK, MessageBoxImage.Information); }
                catch (Exception ex) { System.Windows.MessageBox.Show($"Error de purga:\n{ex.Message}", "Aviso", MessageBoxButton.OK, MessageBoxImage.Warning); }
                finally { btnDesinstalar.IsEnabled = true; btnDesinstalar.Content = "DESINSTALAR"; VerificarEstadoSistema(); btnInstalar.IsEnabled = VerificarSteamInstalado(); }
            }
        }

        private void CrearUsuarioSteam(string nombreUsuario, string passwordTemporal)
        {
            EjecutarComandoOculto($"net user {nombreUsuario} {passwordTemporal} /add /y");
            EjecutarComandoOculto($"wmic useraccount where \"name='{nombreUsuario}'\" set PasswordExpires=FALSE");
            EjecutarComandoOculto($"net localgroup Administradores {nombreUsuario} /add");
            EjecutarComandoOculto($"net localgroup Administrators {nombreUsuario} /add");
            try { using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList")) { key?.SetValue(nombreUsuario, 1, RegistryValueKind.DWord); } } catch { }
        }

        private void EliminarUsuarioSteamOS()
        {
            string sid = ObtenerSidUsuario("SteamOS");
            if (!string.IsNullOrEmpty(sid)) { DeleteProfile(sid, null, null); }
            EjecutarComandoOculto("net user SteamOS /delete");
            try { Directory.Delete(@"C:\Users\SteamOS", true); } catch { }
        }

        private void EjecutarComandoOculto(string comando) { try { ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", $"/c {comando}") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true, UseShellExecute = false }; Process.Start(psi)?.WaitForExit(); } catch { } }
        private string ObtenerSidUsuario(string nombreUsuario) { try { var cuenta = new NTAccount(nombreUsuario); var sid = (SecurityIdentifier)cuenta.Translate(typeof(SecurityIdentifier)); return sid.Value; } catch { return ""; } }

        private void GuardarConfiguracionJson(int indiceMonitor, string resolucion, string refresco, string audioPreferido, bool emuActivado)
        {
            // Protegemos el guardado si el usuario escribió mal
            int w = 1920, h = 1080, hz = 60;
            try
            {
                string[] partesRes = resolucion.Split('x');
                w = int.Parse(partesRes[0].Trim()); h = int.Parse(partesRes[1].Trim());
                hz = int.Parse(refresco.Replace("Hz", "").Trim());
            } catch { }

            var config = new { MonitorDeviceName = Screen.AllScreens[indiceMonitor].DeviceName, ResolucionWidth = w, ResolucionHeight = h, RefreshRate = hz, AudioDispositivo = audioPreferido, EmuladorActivado = emuActivado };
            string rutaConfig = Path.Combine(@"C:\ProgramData\SteamOS", "config.json");
            File.WriteAllText(rutaConfig, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }

        private void ConfigurarIconoSteamOS(string sidUsuario)
        {
            try
            {
                string rutaSteam = @"C:\Program Files (x86)\Steam\steam.exe"; 
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam")) { if (key != null) rutaSteam = Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe"); }
                if (File.Exists(rutaSteam))
                {
                    string rutaAvatar = @"C:\ProgramData\SteamOS\avatar.png"; IntPtr[] phicon = new IntPtr[1]; int[] piconid = new int[1];
                    int result = PrivateExtractIcons(rutaSteam, 0, 256, 256, phicon, piconid, 1, 0);
                    if (result > 0 && phicon[0] != IntPtr.Zero) { using (System.Drawing.Icon icon = System.Drawing.Icon.FromHandle(phicon[0])) using (System.Drawing.Bitmap bitmap = icon.ToBitmap()) { bitmap.Save(rutaAvatar, System.Drawing.Imaging.ImageFormat.Png); } DestroyIcon(phicon[0]); }
                    using (RegistryKey key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sidUsuario}")) { if (key != null) { foreach(string size in new[] { "32", "40", "48", "96", "192", "200", "240", "448" }) key.SetValue($"Image{size}", rutaAvatar, RegistryValueKind.String); } }
                }
            } catch { }
        }

        private void OptimizarInicioNuevoUsuario()
        {
            try { using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System")) key?.SetValue("EnableFirstLogonAnimation", 0, RegistryValueKind.DWord); using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\OOBE")) key?.SetValue("DisablePrivacyExperience", 1, RegistryValueKind.DWord); } catch { }
        }

        private string InstalarEjecutableEnRutaSegura()
        {
            string rutaOrigen = Environment.ProcessPath ?? throw new Exception("No se pudo detectar la ruta del ejecutable principal.");
            string carpetaDestino = @"C:\ProgramData\SteamOS"; string rutaDestino = Path.Combine(carpetaDestino, "WindowsLikeSteamOS.exe");
            if (!Directory.Exists(carpetaDestino)) Directory.CreateDirectory(carpetaDestino);
            File.Copy(rutaOrigen, rutaDestino, true);
            return rutaDestino;
        }

        private void ConstruirPerfilEnSegundoPlano(string usuario, string contrasena, string rutaEjecutable)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!LogonUser(usuario, ".", contrasena, LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out token)) throw new Exception("Error de token.");
                PROFILEINFO p = new PROFILEINFO { dwSize = Marshal.SizeOf(typeof(PROFILEINFO)), lpUserName = usuario };
                if (LoadUserProfile(token, ref p))
                {
                    string sid = ObtenerSidUsuario(usuario);
                    using (RegistryKey? key = Registry.Users.CreateSubKey($@"{sid}\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                    {
                        if (key != null) { key.SetValue("Shell", $"\"{rutaEjecutable}\" -shell", RegistryValueKind.String); key.Flush(); }
                    }
                    UnloadUserProfile(token, p.hProfile);
                }
            }
            finally { if (token != IntPtr.Zero) CloseHandle(token); }
        }
    }
}