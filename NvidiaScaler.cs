// NvidiaScaler.cs
// Fuerza GPU Scaling a Full Panel mediante NVAPI (nvapi64.dll)
// Compatible con WindowsLikeSteamOS / Shell sin explorer.exe
// Requiere: nvapi64.dll en PATH o junto al ejecutable (se instala con los drivers NVIDIA)
//
// IMPORTANTE: NVAPI usa un sistema de "function IDs" con QueryInterface en lugar de
// exports directos. NvAPI_QueryInterface es el ÚNICO export real; el resto son wrappers.
// Este archivo implementa ese patrón correctamente.

using System;
using System.Runtime.InteropServices;

namespace SteamOSConfigurator
{
    /// <summary>
    /// Controla el GPU Scaling de NVIDIA via NVAPI para forzar pantalla completa
    /// en juegos que usen Exclusive Fullscreen a resoluciones menores que el escritorio.
    /// </summary>
    public static class NvidiaScaler
    {
        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 1: NVAPI Function IDs (IDs oficiales del SDK de NVIDIA)
        // ─────────────────────────────────────────────────────────────────────────
        private const uint NVAPI_INITIALIZE_ID                     = 0x0150E828;
        private const uint NVAPI_UNLOAD_ID                         = 0xD22BDD7E;
        private const uint NVAPI_ENUM_PHYSICAL_GPUS_ID             = 0xE5AC921F;
        private const uint NVAPI_ENUM_NVIDIADISPLAYHANDLE_ID       = 0x9ABDD40D;
        private const uint NVAPI_DISP_GET_DISPLAY_CONFIG_ID        = 0x11ABCCF8;
        private const uint NVAPI_DISP_SET_DISPLAY_CONFIG_ID        = 0x5D8CF8DE;
        private const uint NVAPI_GPU_GET_CONNECTED_DISPLAYS_EX_ID  = 0x07E27DA4;

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 2: EL ÚNICO DllImport REAL de nvapi64.dll
        // NvAPI_QueryInterface devuelve un puntero a función; los demás se obtienen así.
        // ─────────────────────────────────────────────────────────────────────────
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr NvAPI_QueryInterface(uint functionId);

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 3: DELEGATES (firmas de las funciones obtenidas via QueryInterface)
        // ─────────────────────────────────────────────────────────────────────────
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate NvStatus NvAPI_InitializeDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate NvStatus NvAPI_UnloadDelegate();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate NvStatus NvAPI_EnumNvidiaDisplayHandleDelegate(
            int thisEnum,
            out IntPtr pNvDispHandle);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate NvStatus NvAPI_Disp_GetDisplayConfigDelegate(
            ref uint pathInfoCount,
            IntPtr pathInfo); // NULL en primera llamada para obtener count

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate NvStatus NvAPI_Disp_SetDisplayConfigDelegate(
            uint pathInfoCount,
            IntPtr pathInfo,
            NvDisplayConfigFlags flags);

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 4: ENUMERACIONES NVAPI
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>Códigos de retorno de NVAPI</summary>
        public enum NvStatus : int
        {
            Ok                          =  0,
            Error                       = -1,
            LibraryNotFound             = -2,
            NoImplementation            = -3,
            ApiNotInitialized           = -4,
            InvalidArgument             = -5,
            NvidiaDeviceNotFound        = -6,
            EndEnumeration              = -7,
            InvalidHandle               = -8,
            IncompatibleStructVersion   = -9,
            NoActiveSLITopology         = -10,
            SLIRenderingModeNotAllowed  = -11,
            NoHandleFound               = -12,
            NotSupported                = -104,
        }

        /// <summary>Modos de escalado GPU (NV_SCALING)</summary>
        public enum NvScaling : uint
        {
            Default                     = 0,  // Usa la configuración actual del panel
            Monitor                     = 1,  // Escalado por monitor (panel propio)
            GpuScalingToClosest         = 2,  // GPU Scaling manteniendo aspecto ratio
            GpuScalingToNative          = 3,  // GPU Scaling a resolución nativa
            GpuScanoutToNative          = 4,
            GpuScalingToAspectScanout   = 5,
            GpuScalingToClosestScanout  = 6,
            GpuScalingToFullPanel       = 7,  // ← ESTE ES EL QUE NECESITAS (Stretch/Full Panel)
            GpuCustomScale              = 8,
            CustomAspectRatio           = 9,
        }

        /// <summary>Flags para SetDisplayConfig</summary>
        [Flags]
        public enum NvDisplayConfigFlags : uint
        {
            None                = 0x00000000,
            Validate            = 0x00000001, // Solo valida, no aplica
            Save                = 0x00000002, // Guarda en NVAPI persistent storage
            DriverReload        = 0x00000004,
            ForceMonoSync       = 0x00000008,
            EnforceModeset      = 0x00000010, // Fuerza modeset aunque no haya cambio
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 5: ESTRUCTURAS NATIVAS (traducidas de nvapi.h)
        // ─────────────────────────────────────────────────────────────────────────

        // Tamaño del campo version: siempre es (sizeof(struct) | (version_number << 16))
        // Para NV_DISPLAYCONFIG_PATH_INFO_V2: version = 2
        private const uint NV_DISPLAYCONFIG_PATH_INFO_VER2 = 2;
        private const uint NV_DISPLAYCONFIG_PATH_TARGET_INFO_VER2 = 2;
        private const uint NV_DISPLAYCONFIG_SOURCE_MODE_INFO_VER1 = 1;

        /// <summary>
        /// NV_DISPLAYCONFIG_PATH_INFO_V2
        /// Describe un path completo: source → target (monitor físico)
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct NV_DISPLAYCONFIG_PATH_INFO
        {
            public uint version;                    // Debe ser NV_DISPLAYCONFIG_PATH_INFO_VER2
            public uint sourceId;                   // ID del source (display adapter output)
            public uint targetInfoCount;            // Número de targets en pTargetInfo
            public IntPtr pTargetInfo;              // Puntero a array de NV_DISPLAYCONFIG_PATH_TARGET_INFO
            public IntPtr pSourceModeInfo;          // Puntero a NV_DISPLAYCONFIG_SOURCE_MODE_INFO (puede ser null)
            [MarshalAs(UnmanagedType.U1)]
            public bool bCloneGroup;               // true si este source forma parte de un clone group
        }

        /// <summary>
        /// NV_DISPLAYCONFIG_PATH_TARGET_INFO_V2
        /// Describe un target (monitor) dentro de un path.
        /// Aquí es donde vive la configuración de scaling.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct NV_DISPLAYCONFIG_PATH_TARGET_INFO
        {
            public uint version;                    // NV_DISPLAYCONFIG_PATH_TARGET_INFO_VER2
            public uint displayId;                  // ID del display (único por conector físico)
            public IntPtr pDetails;                 // Puntero a NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO
            public uint targetFlags;               // Reservado, poner a 0
        }

        /// <summary>
        /// NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO_V1
        /// Contiene los detalles avanzados del target, INCLUYENDO EL SCALING.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO
        {
            public uint version;                    // Use MakeVersion(sizeof, 1)
            public uint refreshRate1K;             // Refresh rate * 1000 (ej: 60000 = 60Hz, 59940 = 59.94Hz)
            public uint tvFormat;                  // NV_DISPLAY_TV_FORMAT, 0 si no es TV
            public uint connector;                 // NV_MONITOR_CONN_TYPE
            public uint scanLineOrdering;          // NV_DISPLAY_SCANLINE_ORDERING
            public NvScaling scaling;              // ← AQUÍ SE CONFIGURA EL SCALING
            public uint rotation;                  // NV_ROTATE (0=0°, 1=90°, 2=180°, 3=270°)
            public uint cloneImportance;           // Para clone groups
            public uint connectionType;            // NV_TARGETINFO_FLAGS
        }

        /// <summary>
        /// NV_DISPLAYCONFIG_SOURCE_MODE_INFO_V1
        /// Describe la resolución y color del source.
        /// </summary>
        [StructLayout(LayoutKind.Sequential, Pack = 8)]
        public struct NV_DISPLAYCONFIG_SOURCE_MODE_INFO
        {
            public uint version;
            public NV_RESOLUTION resolution;
            public NV_FORMAT colorFormat;
            public NV_POSITION position;
            public uint bGDIPrimary;               // 1 si es el display primario de GDI
            public uint bInterlaced;               // 1 si es entrelazado
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct NV_RESOLUTION
        {
            public uint width;
            public uint height;
            public uint colorDepth;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 4)]
        public struct NV_POSITION
        {
            public int x;
            public int y;
        }

        public enum NV_FORMAT : uint
        {
            Unknown = 0,
            P8     = 41,  // 8bpp indexed
            R5G6B5 = 23,  // 16bpp
            A8R8G8B8 = 21, // 32bpp (más común en desktop)
            X8R8G8B8 = 22,
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 6: HELPERS PARA OBTENER FUNCIONES VIA QUERYINTERFACE
        // ─────────────────────────────────────────────────────────────────────────
        private static T GetFunction<T>(uint functionId) where T : Delegate
        {
            IntPtr ptr = NvAPI_QueryInterface(functionId);
            if (ptr == IntPtr.Zero)
                throw new EntryPointNotFoundException(
                    $"NVAPI: QueryInterface retornó null para FunctionID 0x{functionId:X8}. " +
                    $"Verifica que nvapi64.dll esté presente y los drivers estén actualizados.");
            return Marshal.GetDelegateForFunctionPointer<T>(ptr);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 7: MÉTODO PRINCIPAL PÚBLICO
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Fuerza el GPU Scaling a "Full Panel" (Stretch) en todos los displays activos.
        /// Llamar justo antes de lanzar Steam o cualquier juego en Exclusive Fullscreen.
        /// </summary>
        /// <param name="scaling">Modo de escalado deseado. Default: GpuScalingToFullPanel</param>
        /// <param name="targetDisplayId">
        /// ID del display específico a configurar. 0 = todos los displays activos.
        /// </param>
        /// <returns>true si se aplicó con éxito, false si hubo error.</returns>
        public static bool ForzarEscaladoCompleto(
            NvScaling scaling = NvScaling.GpuScalingToFullPanel,
            uint targetDisplayId = 0)
        {
            bool initialized = false;
            NvAPI_UnloadDelegate? nvUnload = null;

            try
            {
                // ── PASO 1: Obtener funciones via QueryInterface ──────────────────
                var nvInit       = GetFunction<NvAPI_InitializeDelegate>       (NVAPI_INITIALIZE_ID);
                nvUnload         = GetFunction<NvAPI_UnloadDelegate>           (NVAPI_UNLOAD_ID);
                var nvGetConfig  = GetFunction<NvAPI_Disp_GetDisplayConfigDelegate>(NVAPI_DISP_GET_DISPLAY_CONFIG_ID);
                var nvSetConfig  = GetFunction<NvAPI_Disp_SetDisplayConfigDelegate>(NVAPI_DISP_SET_DISPLAY_CONFIG_ID);
                var nvEnumDisp   = GetFunction<NvAPI_EnumNvidiaDisplayHandleDelegate>(NVAPI_ENUM_NVIDIADISPLAYHANDLE_ID);

                // ── PASO 2: Inicializar NVAPI ─────────────────────────────────────
                NvStatus status = nvInit();
                ThrowIfError(status, "NvAPI_Initialize");
                initialized = true;
                Console.WriteLine("[NvidiaScaler] NVAPI inicializado correctamente.");

                // ── PASO 3: Obtener conteo de paths (primera llamada con pInfo=NULL) 
                uint pathCount = 0;
                status = nvGetConfig(ref pathCount, IntPtr.Zero);
                ThrowIfError(status, "NvAPI_Disp_GetDisplayConfig (count)");
                Console.WriteLine($"[NvidiaScaler] Display paths encontrados: {pathCount}");

                if (pathCount == 0)
                {
                    Console.WriteLine("[NvidiaScaler] ADVERTENCIA: No se encontraron paths de display.");
                    return false;
                }

                // ── PASO 4: Alocar estructuras y llenar con GetDisplayConfig ───────
                int pathInfoSize    = Marshal.SizeOf<NV_DISPLAYCONFIG_PATH_INFO>();
                int targetInfoSize  = Marshal.SizeOf<NV_DISPLAYCONFIG_PATH_TARGET_INFO>();
                int advancedSize    = Marshal.SizeOf<NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO>();
                int sourceModeSize  = Marshal.SizeOf<NV_DISPLAYCONFIG_SOURCE_MODE_INFO>();

                // Alocamos memoria nativa para el array de PATH_INFO
                IntPtr pathInfoArray = Marshal.AllocHGlobal(pathInfoSize * (int)pathCount);

                try
                {
                    // Inicializar cada PATH_INFO con su version y alocar sub-estructuras
                    IntPtr[] targetInfoPtrs  = new IntPtr[pathCount];
                    IntPtr[] advancedPtrs    = new IntPtr[pathCount];
                    IntPtr[] sourceModePtrs  = new IntPtr[pathCount];

                    for (int i = 0; i < pathCount; i++)
                    {
                        IntPtr pathPtr = IntPtr.Add(pathInfoArray, i * pathInfoSize);

                        // Alocar 1 target por path (asumción inicial; GetConfig lo corregirá)
                        targetInfoPtrs[i] = Marshal.AllocHGlobal(targetInfoSize);
                        advancedPtrs[i]   = Marshal.AllocHGlobal(advancedSize);
                        sourceModePtrs[i] = Marshal.AllocHGlobal(sourceModeSize);

                        // Inicializar el target con su version
                        var targetInfo = new NV_DISPLAYCONFIG_PATH_TARGET_INFO
                        {
                            version  = MakeNvVersion((uint)targetInfoSize, NV_DISPLAYCONFIG_PATH_TARGET_INFO_VER2),
                            pDetails = advancedPtrs[i]
                        };
                        Marshal.StructureToPtr(targetInfo, targetInfoPtrs[i], false);

                        // Inicializar advanced target con version
                        var advancedInfo = new NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO
                        {
                            version = MakeNvVersion((uint)advancedSize, 1)
                        };
                        Marshal.StructureToPtr(advancedInfo, advancedPtrs[i], false);

                        // Inicializar source mode info
                        var sourceModeInfo = new NV_DISPLAYCONFIG_SOURCE_MODE_INFO
                        {
                            version = MakeNvVersion((uint)sourceModeSize, NV_DISPLAYCONFIG_SOURCE_MODE_INFO_VER1)
                        };
                        Marshal.StructureToPtr(sourceModeInfo, sourceModePtrs[i], false);

                        // Inicializar el PATH_INFO y apuntar a los sub-structs
                        var pathInfo = new NV_DISPLAYCONFIG_PATH_INFO
                        {
                            version         = MakeNvVersion((uint)pathInfoSize, NV_DISPLAYCONFIG_PATH_INFO_VER2),
                            targetInfoCount = 1,
                            pTargetInfo     = targetInfoPtrs[i],
                            pSourceModeInfo = sourceModePtrs[i]
                        };
                        Marshal.StructureToPtr(pathInfo, pathPtr, false);
                    }

                    // ── PASO 5: Llamada real a GetDisplayConfig para leer datos actuales
                    status = nvGetConfig(ref pathCount, pathInfoArray);
                    ThrowIfError(status, "NvAPI_Disp_GetDisplayConfig (fill)");

                    // ── PASO 6: Modificar el scaling en cada target ────────────────
                    bool anyModified = false;

                    for (int i = 0; i < pathCount; i++)
                    {
                        IntPtr pathPtr  = IntPtr.Add(pathInfoArray, i * pathInfoSize);
                        var pathInfo    = Marshal.PtrToStructure<NV_DISPLAYCONFIG_PATH_INFO>(pathPtr);
                        uint tCount     = pathInfo.targetInfoCount;

                        for (int t = 0; t < tCount; t++)
                        {
                            IntPtr targetPtr  = IntPtr.Add(pathInfo.pTargetInfo, t * targetInfoSize);
                            var targetInfo    = Marshal.PtrToStructure<NV_DISPLAYCONFIG_PATH_TARGET_INFO>(targetPtr);

                            // Filtro opcional por displayId
                            if (targetDisplayId != 0 && targetInfo.displayId != targetDisplayId)
                                continue;

                            if (targetInfo.pDetails == IntPtr.Zero)
                            {
                                Console.WriteLine($"[NvidiaScaler] Target {t} path {i}: pDetails es null, saltando.");
                                continue;
                            }

                            var advInfo = Marshal.PtrToStructure<NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO>(targetInfo.pDetails);

                            Console.WriteLine($"[NvidiaScaler] Display ID {targetInfo.displayId}: " +
                                              $"Scaling actual = {advInfo.scaling} → Nuevo = {scaling}");

                            advInfo.scaling = scaling;
                            Marshal.StructureToPtr(advInfo, targetInfo.pDetails, false);
                            anyModified = true;
                        }
                    }

                    if (!anyModified)
                    {
                        Console.WriteLine("[NvidiaScaler] No se modificó ningún target " +
                                          $"(targetDisplayId={targetDisplayId}). Verifica el Display ID.");
                        return false;
                    }

                    // ── PASO 7: Aplicar la nueva configuración ─────────────────────
                    // Flags: Save persiste el cambio, EnforceModeset fuerza aplicación
                    NvDisplayConfigFlags flags = NvDisplayConfigFlags.Save | NvDisplayConfigFlags.EnforceModeset;

                    status = nvSetConfig(pathCount, pathInfoArray, flags);
                    ThrowIfError(status, "NvAPI_Disp_SetDisplayConfig");

                    Console.WriteLine($"[NvidiaScaler] ✓ GPU Scaling configurado a: {scaling}");
                    return true;
                }
                finally
                {
                    // Liberar toda la memoria nativa alocada
                    for (int i = 0; i < pathCount; i++)
                    {
                        IntPtr pathPtr = IntPtr.Add(pathInfoArray, i * pathInfoSize);
                        var pathInfo = Marshal.PtrToStructure<NV_DISPLAYCONFIG_PATH_INFO>(pathPtr);

                        if (pathInfo.pTargetInfo != IntPtr.Zero)
                        {
                            uint tCount = pathInfo.targetInfoCount;
                            for (int t = 0; t < tCount; t++)
                            {
                                IntPtr targetPtr = IntPtr.Add(pathInfo.pTargetInfo, t * targetInfoSize);
                                var targetInfo   = Marshal.PtrToStructure<NV_DISPLAYCONFIG_PATH_TARGET_INFO>(targetPtr);
                                if (targetInfo.pDetails != IntPtr.Zero)
                                    Marshal.FreeHGlobal(targetInfo.pDetails);
                            }
                            Marshal.FreeHGlobal(pathInfo.pTargetInfo);
                        }

                        if (pathInfo.pSourceModeInfo != IntPtr.Zero)
                            Marshal.FreeHGlobal(pathInfo.pSourceModeInfo);
                    }
                    Marshal.FreeHGlobal(pathInfoArray);
                }
            }
            catch (EntryPointNotFoundException ex)
            {
                Console.Error.WriteLine($"[NvidiaScaler] ERROR: {ex.Message}");
                Console.Error.WriteLine("[NvidiaScaler] Asegúrate de que nvapi64.dll esté en el PATH " +
                                        "o en el directorio del ejecutable.");
                return false;
            }
            catch (NvApiException ex)
            {
                Console.Error.WriteLine($"[NvidiaScaler] NVAPI Error en {ex.FunctionName}: {ex.Status} ({(int)ex.Status})");
                Console.Error.WriteLine(GetNvStatusHint(ex.Status));
                return false;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NvidiaScaler] ERROR inesperado: {ex}");
                return false;
            }
            finally
            {
                // Siempre descargar NVAPI si se inicializó
                if (initialized && nvUnload != null)
                {
                    nvUnload();
                    Console.WriteLine("[NvidiaScaler] NVAPI descargado.");
                }
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 8: MÉTODO DE RESTAURACIÓN (útil para salir de un juego)
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Restaura el scaling al modo "por monitor" (comportamiento nativo del panel).
        /// Útil para llamar al CERRAR un juego si quieres revertir el scaling.
        /// </summary>
        public static bool RestaurarEscaladoPorMonitor(uint targetDisplayId = 0)
        {
            return ForzarEscaladoCompleto(NvScaling.Monitor, targetDisplayId);
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 9: MÉTODO DIAGNÓSTICO - Lista todos los displays con sus IDs
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Lista todos los displays NVIDIA activos con su displayId y scaling actual.
        /// Útil para obtener el displayId correcto si tienes múltiples monitores.
        /// </summary>
        public static void ListarDisplays()
        {
            NvAPI_UnloadDelegate? nvUnload = null;
            bool initialized = false;

            try
            {
                var nvInit      = GetFunction<NvAPI_InitializeDelegate>           (NVAPI_INITIALIZE_ID);
                nvUnload        = GetFunction<NvAPI_UnloadDelegate>               (NVAPI_UNLOAD_ID);
                var nvGetConfig = GetFunction<NvAPI_Disp_GetDisplayConfigDelegate>(NVAPI_DISP_GET_DISPLAY_CONFIG_ID);

                NvStatus status = nvInit();
                ThrowIfError(status, "NvAPI_Initialize");
                initialized = true;

                uint pathCount = 0;
                status = nvGetConfig(ref pathCount, IntPtr.Zero);
                ThrowIfError(status, "NvAPI_Disp_GetDisplayConfig (count)");

                Console.WriteLine($"\n[NvidiaScaler] === Displays NVIDIA detectados ({pathCount} paths) ===");

                int pathInfoSize   = Marshal.SizeOf<NV_DISPLAYCONFIG_PATH_INFO>();
                int targetInfoSize = Marshal.SizeOf<NV_DISPLAYCONFIG_PATH_TARGET_INFO>();
                int advancedSize   = Marshal.SizeOf<NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO>();
                int sourceModeSize = Marshal.SizeOf<NV_DISPLAYCONFIG_SOURCE_MODE_INFO>();

                IntPtr pathInfoArray = Marshal.AllocHGlobal(pathInfoSize * (int)pathCount);

                try
                {
                    // Inicializar structs mínimas para que GetDisplayConfig las llene
                    for (int i = 0; i < pathCount; i++)
                    {
                        IntPtr pathPtr = IntPtr.Add(pathInfoArray, i * pathInfoSize);
                        IntPtr targetPtr   = Marshal.AllocHGlobal(targetInfoSize);
                        IntPtr advPtr      = Marshal.AllocHGlobal(advancedSize);
                        IntPtr srcModePtr  = Marshal.AllocHGlobal(sourceModeSize);

                        Marshal.StructureToPtr(new NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO
                            { version = MakeNvVersion((uint)advancedSize, 1) }, advPtr, false);

                        Marshal.StructureToPtr(new NV_DISPLAYCONFIG_PATH_TARGET_INFO
                            { version  = MakeNvVersion((uint)targetInfoSize, NV_DISPLAYCONFIG_PATH_TARGET_INFO_VER2),
                              pDetails = advPtr }, targetPtr, false);

                        Marshal.StructureToPtr(new NV_DISPLAYCONFIG_SOURCE_MODE_INFO
                            { version = MakeNvVersion((uint)sourceModeSize, NV_DISPLAYCONFIG_SOURCE_MODE_INFO_VER1) },
                            srcModePtr, false);

                        Marshal.StructureToPtr(new NV_DISPLAYCONFIG_PATH_INFO
                            { version         = MakeNvVersion((uint)pathInfoSize, NV_DISPLAYCONFIG_PATH_INFO_VER2),
                              targetInfoCount  = 1,
                              pTargetInfo      = targetPtr,
                              pSourceModeInfo  = srcModePtr }, pathPtr, false);
                    }

                    status = nvGetConfig(ref pathCount, pathInfoArray);
                    ThrowIfError(status, "NvAPI_Disp_GetDisplayConfig (fill)");

                    for (int i = 0; i < pathCount; i++)
                    {
                        IntPtr pathPtr = IntPtr.Add(pathInfoArray, i * pathInfoSize);
                        var pathInfo   = Marshal.PtrToStructure<NV_DISPLAYCONFIG_PATH_INFO>(pathPtr);

                        NV_DISPLAYCONFIG_SOURCE_MODE_INFO srcInfo = default;
                        if (pathInfo.pSourceModeInfo != IntPtr.Zero)
                            srcInfo = Marshal.PtrToStructure<NV_DISPLAYCONFIG_SOURCE_MODE_INFO>(pathInfo.pSourceModeInfo);

                        Console.WriteLine($"\n  Path {i}: SourceID={pathInfo.sourceId} | " +
                                          $"Resolución={srcInfo.resolution.width}x{srcInfo.resolution.height}");

                        for (int t = 0; t < pathInfo.targetInfoCount; t++)
                        {
                            IntPtr tPtr     = IntPtr.Add(pathInfo.pTargetInfo, t * targetInfoSize);
                            var targetInfo  = Marshal.PtrToStructure<NV_DISPLAYCONFIG_PATH_TARGET_INFO>(tPtr);

                            string scalingStr = "N/A";
                            if (targetInfo.pDetails != IntPtr.Zero)
                            {
                                var adv    = Marshal.PtrToStructure<NV_DISPLAYCONFIG_PATH_ADVANCED_TARGET_INFO>(targetInfo.pDetails);
                                scalingStr = adv.scaling.ToString();
                                Marshal.FreeHGlobal(targetInfo.pDetails);
                            }

                            Console.WriteLine($"    Target {t}: DisplayID=0x{targetInfo.displayId:X8} | Scaling={scalingStr}");
                            Marshal.FreeHGlobal(tPtr);
                        }

                        if (pathInfo.pSourceModeInfo != IntPtr.Zero)
                            Marshal.FreeHGlobal(pathInfo.pSourceModeInfo);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(pathInfoArray);
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[NvidiaScaler] Error en ListarDisplays: {ex.Message}");
            }
            finally
            {
                if (initialized && nvUnload != null)
                    nvUnload();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 10: HELPERS PRIVADOS
        // ─────────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Construye el campo 'version' de las structs NVAPI.
        /// Fórmula: (structSize & 0xFFFF) | (versionNumber << 16)
        /// </summary>
        private static uint MakeNvVersion(uint structSize, uint versionNumber)
            => (structSize & 0xFFFF) | (versionNumber << 16);

        private static void ThrowIfError(NvStatus status, string functionName)
        {
            if (status != NvStatus.Ok && status != NvStatus.EndEnumeration)
                throw new NvApiException(functionName, status);
        }

        private static string GetNvStatusHint(NvStatus status) => status switch
        {
            NvStatus.ApiNotInitialized    => "→ NvAPI_Initialize no fue llamado o falló.",
            NvStatus.NvidiaDeviceNotFound => "→ No se detectó GPU NVIDIA. ¿Están los drivers instalados?",
            NvStatus.InvalidArgument      => "→ Estructura malformada o versión incorrecta. Revisa los campos 'version'.",
            NvStatus.NotSupported         => "→ Esta función no es soportada por el driver actual. Actualiza los drivers.",
            NvStatus.NoHandleFound        => "→ No se encontró handle de display. ¿Está conectado el monitor?",
            NvStatus.IncompatibleStructVersion => "→ La versión de la estructura no coincide. Verifica MakeNvVersion().",
            _ => $"→ Consulta la documentación de NVAPI para el código {(int)status}."
        };

        // ─────────────────────────────────────────────────────────────────────────
        // SECCIÓN 11: EXCEPCIÓN PERSONALIZADA
        // ─────────────────────────────────────────────────────────────────────────
        public class NvApiException : Exception
        {
            public string FunctionName { get; }
            public NvStatus Status     { get; }

            public NvApiException(string functionName, NvStatus status)
                : base($"NVAPI error en {functionName}: {status} ({(int)status})")
            {
                FunctionName = functionName;
                Status       = status;
            }
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // EJEMPLO DE USO (puedes mover esto a tu Shell principal)
    // ─────────────────────────────────────────────────────────────────────────────
    /*
    internal static class ShellEntryPoint
    {
        static void Main()
        {
            // Diagnóstico: ver qué displays hay y sus IDs
            NvidiaScaler.ListarDisplays();

            // Opción A: Forzar Full Panel en TODOS los displays activos
            bool ok = NvidiaScaler.ForzarEscaladoCompleto();

            // Opción B: Forzar solo en un display específico (obtén el ID con ListarDisplays)
            // bool ok = NvidiaScaler.ForzarEscaladoCompleto(targetDisplayId: 0x0001C200);

            // Opción C: Mantener aspect ratio (letterbox/pillarbox) en vez de stretch
            // bool ok = NvidiaScaler.ForzarEscaladoCompleto(NvidiaScaler.NvScaling.GpuScalingToClosest);

            if (ok)
            {
                // Lanzar Steam
                Process.Start("steam.exe");
            }

            // Al cerrar el juego, restaurar (opcional):
            // NvidiaScaler.RestaurarEscaladoPorMonitor();
        }
    }
    */
}
