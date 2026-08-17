using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using Microsoft.Win32;
using SteamOSConfigurator.Helpers;

namespace SteamOSConfigurator.Services
{
    public enum EstadoInstalacion
    {
        NoInstalado,
        ActualizacionDisponible,
        MismaVersion,
        Downgrade,
        InstaladoYEnEjecucion
    }

    public class InfoEstadoInstalacion
    {
        public EstadoInstalacion Estado { get; set; }
        public bool UsuarioSteamOSExiste { get; set; }
        public bool BinarioDestinoExiste { get; set; }
        public DateTime FechaInstalada { get; set; }
        public DateTime FechaInstalador { get; set; }
        public string RutaInstalador { get; set; } = string.Empty;
        public string RutaDestino { get; set; } = string.Empty;
    }

    public static class InstallationService
    {
        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LogonUserW")]
        private static extern bool LogonUser(string lpszUsername, string lpszDomain, string lpszPassword, int dwLogonType, int dwLogonProvider, out IntPtr phToken);

        [DllImport("userenv.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LoadUserProfileW")]
        private static extern bool LoadUserProfile(IntPtr hToken, ref PROFILEINFO lpProfileInfo);

        [DllImport("userenv.dll", SetLastError = true)]
        private static extern bool UnloadUserProfile(IntPtr hToken, IntPtr hProfile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern int PrivateExtractIcons(string lpszFile, int nIconIndex, int cxIcon, int cyIcon, IntPtr[] phicon, int[] piconid, int nIcons, int flags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct PROFILEINFO
        {
            public int dwSize;
            public int dwFlags;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string lpUserName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpProfilePath;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpDefaultPath;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpServerName;
            [MarshalAs(UnmanagedType.LPWStr)]
            public string? lpPolicyPath;
            public IntPtr hProfile;
        }

        private const int LOGON32_LOGON_INTERACTIVE = 2;
        private const int LOGON32_PROVIDER_DEFAULT = 0;
        private const string CLAVE_UNINSTALL_WINDOWS = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SteamOS";

        /// <summary>
        /// Evalúa el estado del sistema y compara el binario actual con el instalado en disco.
        /// </summary>
        public static InfoEstadoInstalacion EvaluarEstado()
        {
            string rutaActual = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "SteamOS.exe");
            string rutaDestino = AppPaths.EjecutableDestino;

            bool usuarioExiste = false;
            try
            {
                var cuenta = new NTAccount("SteamOS");
                cuenta.Translate(typeof(SecurityIdentifier));
                usuarioExiste = true;
            }
            catch { }

            bool binarioDestinoExiste = File.Exists(rutaDestino);
            DateTime fechaInstalada = binarioDestinoExiste ? BuildInfo.ObtenerFechaCompilacion(rutaDestino) : DateTime.MinValue;
            DateTime fechaInstalador = BuildInfo.ObtenerFechaCompilacion(rutaActual);

            var info = new InfoEstadoInstalacion
            {
                UsuarioSteamOSExiste = usuarioExiste,
                BinarioDestinoExiste = binarioDestinoExiste,
                FechaInstalada = fechaInstalada,
                FechaInstalador = fechaInstalador,
                RutaInstalador = rutaActual,
                RutaDestino = rutaDestino
            };

            // Si se está ejecutando directamente desde la carpeta desplegada C:\ProgramData\SteamOS\SteamOS.exe
            if (binarioDestinoExiste && rutaActual.Equals(rutaDestino, StringComparison.OrdinalIgnoreCase))
            {
                info.Estado = EstadoInstalacion.InstaladoYEnEjecucion;
                return info;
            }

            if (!usuarioExiste || !binarioDestinoExiste)
            {
                info.Estado = EstadoInstalacion.NoInstalado;
                return info;
            }

            // Comparar por fecha y hora de compilación (con margen de 5 segundos para evitar discrepancias de redondeo)
            TimeSpan diferencia = fechaInstalador - fechaInstalada;
            if (diferencia.TotalSeconds > 5)
            {
                info.Estado = EstadoInstalacion.ActualizacionDisponible;
            }
            else if (diferencia.TotalSeconds < -5)
            {
                info.Estado = EstadoInstalacion.Downgrade;
            }
            else
            {
                info.Estado = EstadoInstalacion.MismaVersion;
            }

            return info;
        }

        /// <summary>
        /// Ejecuta la instalación limpia o actualización completa del entorno SteamOS.
        /// </summary>
        public static async Task<bool> InstalarOActualizarAsync(Action<string>? onProgreso = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    string carpetaDestino = AppPaths.RaizDatos;

                    onProgreso?.Invoke("Preparando entorno de instalación...");
                    Logger.Log("[InstallationService] Iniciando instalación/actualización modular...");

                    // 1. Cerrar procesos de SteamOS en ejecución para desbloquear archivos
                    CerrarProcesosSteamOS();

                    if (!Directory.Exists(carpetaDestino))
                        Directory.CreateDirectory(carpetaDestino);

                    // 2. Desplegar ejecutables modulares y recursos
                    onProgreso?.Invoke("Desplegando componentes del sistema (Shell y Configuración)...");
                    ExtraerRecursoOArchivo("SteamOS_Shell.exe", AppPaths.ShellExe);
                    ExtraerRecursoOArchivo("SteamOS_Config.exe", AppPaths.ConfigExe);
                    ExtraerRecursoOArchivo("icon.ico", AppPaths.Icon);
                    ExtraerRecursoOArchivo("avatar.png", AppPaths.Avatar);
                    ExtraerRecursoOArchivo("juegos_perfiles.json", Path.Combine(carpetaDestino, "juegos_perfiles.json"));

                    // 3. Crear usuario y configurar perfil si no existe
                    var estado = EvaluarEstado();
                    if (!estado.UsuarioSteamOSExiste)
                    {
                        onProgreso?.Invoke("Creando cuenta de usuario SteamOS...");
                        string passwordTemporal = "SteamOS123!";
                        CrearUsuarioSteam("SteamOS", passwordTemporal);
                        OptimizarInicioNuevoUsuario();

                        onProgreso?.Invoke("Configurando perfil de consola...");
                        ConstruirPerfilEnSegundoPlano("SteamOS", passwordTemporal, AppPaths.ShellExe);

                        string sid = ObtenerSidUsuario("SteamOS");
                        if (!string.IsNullOrEmpty(sid))
                            ConfigurarIconoSteamOS(sid);
                    }

                    // Asegurar SIEMPRE que la Shell del usuario SteamOS apunte a SteamOS_Shell.exe
                    onProgreso?.Invoke("Configurando shell de consola...");
                    ConfigurarShellUsuario("SteamOS", AppPaths.ShellExe);

                    // 4. Configurar Autologin sin contraseña
                    onProgreso?.Invoke("Configurando inicio de sesión automático...");
                    ConfigurarAutologin("SteamOS", true);

                    // 5. Crear accesos directos profesionales (Escritorio + Menú Inicio)
                    onProgreso?.Invoke("Creando accesos directos...");
                    CrearAccesosDirectos(AppPaths.ConfigExe, AppPaths.Icon);

                    // 6. Registrar en Configuración / Panel de Control de Windows (Apps instaladas)
                    onProgreso?.Invoke("Registrando en el sistema operativo...");
                    RegistrarEnAplicacionesWindows(AppPaths.ConfigExe, AppPaths.Icon);

                    Logger.Log("[InstallationService] Instalación modular completada con éxito.");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[InstallationService] Error en la instalación: {ex.Message}");
                    throw;
                }
            });
        }

        public static bool ExtraerRecursoOArchivo(string nombreArchivo, string rutaDestino)
        {
            try
            {
                // 1. Primero intentar extraer de los recursos incrustados del ensamblado
                var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                string[] manifestNames = assembly.GetManifestResourceNames();
                string? matchedName = null;

                foreach (var name in manifestNames)
                {
                    if (string.Equals(name, nombreArchivo, StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith("." + nombreArchivo, StringComparison.OrdinalIgnoreCase))
                    {
                        matchedName = name;
                        break;
                    }
                }

                if (matchedName != null)
                {
                    using (var stream = assembly.GetManifestResourceStream(matchedName))
                    {
                        if (stream != null)
                        {
                            using (var fileStream = new FileStream(rutaDestino, FileMode.Create, FileAccess.Write, FileShare.None))
                            {
                                stream.CopyTo(fileStream);
                            }
                            Logger.Log($"[InstallationService] Recurso incrustado '{matchedName}' extraído a '{rutaDestino}'.");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[InstallationService] Aviso al extraer recurso '{nombreArchivo}': {ex.Message}");
            }

            // 2. Si no estaba incrustado, buscarlo como archivo suelto en la carpeta del ejecutable o subdirectorios
            try
            {
                string dirActual = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string[] posiblesRutas = new[]
                {
                    Path.Combine(dirActual, nombreArchivo),
                    Path.Combine(AppContext.BaseDirectory, nombreArchivo),
                    Path.Combine(dirActual, "bin", nombreArchivo),
                    Path.Combine(dirActual, "Release", nombreArchivo),
                    Path.Combine(dirActual, "net8.0-windows", nombreArchivo),
                    Path.Combine(dirActual, "..", "SteamOS.Shell", "bin", "Release", "net8.0-windows", nombreArchivo),
                    Path.Combine(dirActual, "..", "SteamOS.Config", "bin", "Release", "net8.0-windows", nombreArchivo),
                    Path.Combine(dirActual, "..", "..", "..", "SteamOS.Shell", "bin", "Release", "net8.0-windows", nombreArchivo),
                    Path.Combine(dirActual, "..", "..", "..", "SteamOS.Config", "bin", "Release", "net8.0-windows", nombreArchivo),
                    Path.Combine(AppPaths.RaizDatos, nombreArchivo)
                };

                foreach (var ruta in posiblesRutas)
                {
                    if (File.Exists(ruta) && !string.Equals(Path.GetFullPath(ruta), Path.GetFullPath(rutaDestino), StringComparison.OrdinalIgnoreCase))
                    {
                        File.Copy(ruta, rutaDestino, true);
                        Logger.Log($"[InstallationService] Archivo '{ruta}' copiado a '{rutaDestino}'.");
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[InstallationService] Error al copiar archivo '{nombreArchivo}': {ex.Message}");
            }

            return false;
        }

        /// <summary>
        /// Cierra procesos activos de SteamOS para no bloquear reemplazo de binarios.
        /// </summary>
        public static void CerrarProcesosSteamOS()
        {
            try
            {
                int currentPid = Environment.ProcessId;
                foreach (var p in Process.GetProcessesByName("SteamOS"))
                {
                    if (p.Id != currentPid)
                    {
                        try { p.Kill(); p.Dispose(); } catch { }
                    }
                }
                foreach (var p in Process.GetProcessesByName("WindowsLikeSteamOS"))
                {
                    if (p.Id != currentPid)
                    {
                        try { p.Kill(); p.Dispose(); } catch { }
                    }
                }
            }
            catch { }
        }

        public static void ConfigurarAutologin(string nombreUsuario, bool autoLogon)
        {
            try
            {
                // Remover contraseña de la cuenta para permitir logoff/logon sin clave
                EjecutarComandoOculto($"net user {nombreUsuario} \"\"");

                using (RegistryKey? pwlessKey = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\PasswordLess\Device"))
                {
                    pwlessKey?.SetValue("DevicePasswordLessBuildVersion", 0, RegistryValueKind.DWord);
                }

                using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                {
                    if (key != null)
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
            }
            catch (Exception ex)
            {
                Logger.Log($"[InstallationService] Error configurando autologin: {ex.Message}");
            }
        }

        public static void ConfigurarAutoAdminLogon(bool autoLogon)
        {
            ConfigurarAutologin("SteamOS", autoLogon);
        }

        public static void CrearAccesosDirectos(string? rutaExe = null, string? rutaIcono = null)
        {
            try
            {
                rutaExe ??= AppPaths.ConfigExe;
                rutaIcono ??= AppPaths.Icon;
                string iconScript = File.Exists(rutaIcono) ? $"$s.IconLocation = '{rutaIcono}';" : "";

                // 1. Acceso directo en Escritorio Público
                string desktopPublico = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                string lnkDesktop = Path.Combine(desktopPublico, "SteamOS.lnk");
                string oldLnk = Path.Combine(desktopPublico, "Configurar WindowsLikeSteamOS.lnk");
                if (File.Exists(oldLnk)) try { File.Delete(oldLnk); } catch { }

                // 2. Acceso directo en Menú Inicio Público
                string startMenuPublico = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
                string programsDir = Path.Combine(startMenuPublico, "Programs");
                if (!Directory.Exists(programsDir)) Directory.CreateDirectory(programsDir);
                string lnkStartMenu = Path.Combine(programsDir, "SteamOS.lnk");

                string script = $@"
                    $w = New-Object -comObject WScript.Shell;
                    $s = $w.CreateShortcut('{lnkDesktop}');
                    $s.TargetPath = '{rutaExe}';
                    $s.WorkingDirectory = '{Path.GetDirectoryName(rutaExe)}';
                    {iconScript}
                    $s.Save();
                    $s2 = $w.CreateShortcut('{lnkStartMenu}');
                    $s2.TargetPath = '{rutaExe}';
                    $s2.WorkingDirectory = '{Path.GetDirectoryName(rutaExe)}';
                    {iconScript}
                    $s2.Save();
                ";

                EjecutarComandoOculto($"powershell -Command \"{script.Replace("\r\n", " ").Replace("\n", " ")}\"");
            }
            catch (Exception ex)
            {
                Logger.Log($"[InstallationService] Error creando accesos directos: {ex.Message}");
            }
        }

        public static void RegistrarEnAplicacionesWindows(string rutaExe, string rutaIcono)
        {
            try
            {
                using (var key = Registry.LocalMachine.CreateSubKey(CLAVE_UNINSTALL_WINDOWS))
                {
                    if (key != null)
                    {
                        key.SetValue("DisplayName", "SteamOS", RegistryValueKind.String);
                        key.SetValue("DisplayIcon", File.Exists(rutaIcono) ? rutaIcono : rutaExe, RegistryValueKind.String);
                        key.SetValue("DisplayVersion", "4.3.0", RegistryValueKind.String);
                        key.SetValue("Publisher", "SteamOS for Windows", RegistryValueKind.String);
                        key.SetValue("InstallLocation", Path.GetDirectoryName(rutaExe) ?? "", RegistryValueKind.String);
                        key.SetValue("UninstallString", $"\"{rutaExe}\" -uninstall", RegistryValueKind.String);
                        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[InstallationService] Error registrando en Windows Uninstall: {ex.Message}");
            }
        }

        public static async Task<bool> DesinstalarEntornoAsync(Action<string>? onProgreso = null)
        {
            return await Task.Run(() =>
            {
                try
                {
                    onProgreso?.Invoke("Cerrando procesos de SteamOS...");
                    CerrarProcesosSteamOS();

                    onProgreso?.Invoke("Restaurando configuración de inicio de sesión...");
                    try
                    {
                        using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                        {
                            key?.SetValue("AutoAdminLogon", "0", RegistryValueKind.String);
                            key?.DeleteValue("DefaultUserName", false);
                            key?.DeleteValue("DefaultPassword", false);
                        }
                    }
                    catch { }

                    onProgreso?.Invoke("Eliminando cuenta de usuario SteamOS...");
                    EjecutarComandoOculto("net user SteamOS /delete");

                    onProgreso?.Invoke("Eliminando accesos directos...");
                    try
                    {
                        string desktopPublico = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
                        string lnkDesktop = Path.Combine(desktopPublico, "SteamOS.lnk");
                        if (File.Exists(lnkDesktop)) File.Delete(lnkDesktop);

                        string startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "SteamOS.lnk");
                        if (File.Exists(startMenu)) File.Delete(startMenu);
                    }
                    catch { }

                    onProgreso?.Invoke("Eliminando registro de Windows...");
                    try
                    {
                        Registry.LocalMachine.DeleteSubKeyTree(CLAVE_UNINSTALL_WINDOWS, false);
                    }
                    catch { }

                    onProgreso?.Invoke("Desinstalación finalizada.");
                    return true;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[InstallationService] Error durante desinstalación: {ex.Message}");
                    return false;
                }
            });
        }

        // --- Métodos de bajo nivel para usuario y registro ---
        private static void CrearUsuarioSteam(string nombreUsuario, string passwordTemporal)
        {
            EjecutarComandoOculto($"net user {nombreUsuario} {passwordTemporal} /add /y");
            EjecutarComandoOculto($"wmic useraccount where \"name='{nombreUsuario}'\" set PasswordExpires=FALSE");
            EjecutarComandoOculto($"net localgroup Administradores {nombreUsuario} /add");
            EjecutarComandoOculto($"net localgroup Administrators {nombreUsuario} /add");

            try
            {
                using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon\SpecialAccounts\UserList"))
                {
                    key?.SetValue(nombreUsuario, 1, RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        private static string ObtenerSidUsuario(string nombreUsuario)
        {
            try
            {
                var cuenta = new NTAccount(nombreUsuario);
                var sid = (SecurityIdentifier)cuenta.Translate(typeof(SecurityIdentifier));
                return sid.Value;
            }
            catch
            {
                return "";
            }
        }

        private static void ConfigurarIconoSteamOS(string sidUsuario)
        {
            try
            {
                string rutaSteam = AppPaths.SteamFallback;
                using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(AppPaths.SteamRegistryKey))
                {
                    if (key != null) rutaSteam = Path.Combine(key.GetValue("InstallPath") as string ?? "", "steam.exe");
                }

                if (File.Exists(rutaSteam))
                {
                    string rutaAvatar = AppPaths.Avatar;
                    IntPtr[] phicon = new IntPtr[1];
                    int[] piconid = new int[1];
                    int result = PrivateExtractIcons(rutaSteam, 0, 256, 256, phicon, piconid, 1, 0);
                    if (result > 0 && phicon[0] != IntPtr.Zero)
                    {
                        using (System.Drawing.Icon icon = System.Drawing.Icon.FromHandle(phicon[0]))
                        using (System.Drawing.Bitmap bitmap = icon.ToBitmap())
                        {
                            bitmap.Save(rutaAvatar, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        DestroyIcon(phicon[0]);
                    }

                    using (RegistryKey key = Registry.LocalMachine.CreateSubKey($@"SOFTWARE\Microsoft\Windows\CurrentVersion\AccountPicture\Users\{sidUsuario}"))
                    {
                        if (key != null)
                        {
                            foreach (string size in new[] { "32", "40", "48", "96", "192", "200", "240", "448" })
                                key.SetValue($"Image{size}", rutaAvatar, RegistryValueKind.String);
                        }
                    }
                }
            }
            catch { }
        }

        private static void OptimizarInicioNuevoUsuario()
        {
            try
            {
                using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"))
                    key?.SetValue("EnableFirstLogonAnimation", 0, RegistryValueKind.DWord);

                using (RegistryKey? key = Registry.LocalMachine.CreateSubKey(@"SOFTWARE\Policies\Microsoft\Windows\OOBE"))
                    key?.SetValue("DisablePrivacyExperience", 1, RegistryValueKind.DWord);
            }
            catch { }
        }

        public static void ConfigurarShellUsuario(string usuario, string rutaEjecutable)
        {
            try
            {
                string sid = ObtenerSidUsuario(usuario);
                string valorShell = rutaEjecutable;

                // 1. Si la sesión del usuario está activa o su hive está montado
                if (!string.IsNullOrEmpty(sid))
                {
                    try
                    {
                        using (RegistryKey? key = Registry.Users.OpenSubKey($@"{sid}\Software\Microsoft\Windows NT\CurrentVersion\Winlogon", true))
                        {
                            if (key != null)
                            {
                                key.SetValue("Shell", valorShell, RegistryValueKind.String);
                                key.Flush();
                                Logger.Log($"[InstallationService] Shell configurado en HKU\\{sid} a {valorShell}");
                                return;
                            }
                        }
                    }
                    catch { }
                }

                // 2. Si no está montado, usar reg load y reg add
                string rutaNtuser = $@"C:\Users\{usuario}\NTUSER.DAT";
                if (File.Exists(rutaNtuser))
                {
                    EjecutarComandoOculto($"reg load HKU\\SteamOSTemp \"{rutaNtuser}\"");
                    EjecutarComandoOculto($"reg add \"HKU\\SteamOSTemp\\Software\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon\" /v Shell /t REG_SZ /d \"{valorShell}\" /f");
                    EjecutarComandoOculto("reg unload HKU\\SteamOSTemp");
                    Logger.Log($"[InstallationService] Shell configurado via reg en {rutaNtuser} a {valorShell}");
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"[InstallationService] Error al configurar shell de usuario: {ex.Message}");
            }
        }

        private static void ConstruirPerfilEnSegundoPlano(string usuario, string contrasena, string rutaEjecutable)
        {
            IntPtr token = IntPtr.Zero;
            try
            {
                if (!LogonUser(usuario, ".", contrasena, LOGON32_LOGON_INTERACTIVE, LOGON32_PROVIDER_DEFAULT, out token))
                    throw new Exception("Error al obtener token de logon.");

                PROFILEINFO p = new PROFILEINFO { dwSize = Marshal.SizeOf(typeof(PROFILEINFO)), lpUserName = usuario };
                if (LoadUserProfile(token, ref p))
                {
                    string sid = ObtenerSidUsuario(usuario);
                    using (RegistryKey? key = Registry.Users.CreateSubKey($@"{sid}\Software\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                    {
                        if (key != null)
                        {
                            key.SetValue("Shell", rutaEjecutable, RegistryValueKind.String);
                            key.Flush();
                        }
                    }
                    UnloadUserProfile(token, p.hProfile);
                }
            }
            finally
            {
                if (token != IntPtr.Zero) CloseHandle(token);
            }
        }

        private static void EjecutarComandoOculto(string comando)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", $"/c {comando}")
                {
                    WindowStyle = ProcessWindowStyle.Hidden,
                    CreateNoWindow = true,
                    UseShellExecute = false
                };
                Process.Start(psi)?.WaitForExit();
            }
            catch { }
        }
    }
}
