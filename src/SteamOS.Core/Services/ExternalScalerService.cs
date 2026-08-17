using System;
using System.Threading;
using System.Threading.Tasks;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SteamOSConfigurator;

namespace WindowsLikeSteamOS.Services
{
    public sealed class ExternalScalerService : IDisposable
    {
        private static readonly Lazy<ExternalScalerService> _lazyInstance = new Lazy<ExternalScalerService>(() => new ExternalScalerService());
        public static ExternalScalerService Instance => _lazyInstance.Value;

        private SharpDX.Direct3D11.Device? _d3dDevice;
        private SharpDX.Direct3D11.Texture2D? _sharedTexture;
        private KeyedMutex? _keyedMutex;
        private bool _isScalingActive;
        private CancellationTokenSource? _cts;
        private IntPtr _lastHandle = IntPtr.Zero;

        private ExternalScalerService()
        {
            InitializeD3D();
        }

        private void InitializeD3D()
        {
            try
            {
                _d3dDevice = new SharpDX.Direct3D11.Device(
                    DriverType.Hardware,
                    DeviceCreationFlags.BgraSupport,
                    FeatureLevel.Level_11_0
                );
                Logger.Log("[ExternalScalerService] Dispositivo D3D11 inicializado correctamente.");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ExternalScalerService] Error inicializando D3D11: {ex.Message}");
            }
        }

        public void StartScaling()
        {
            if (_isScalingActive) return;
            _isScalingActive = true;
            _cts = new CancellationTokenSource();

            Task.Run(() => ScalerLoopAsync(_cts.Token));
            Logger.Log("[ExternalScalerService] Bucle de escalado externo iniciado.");
        }

        public void StopScaling()
        {
            _isScalingActive = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;

            CleanupSharedResources();
            Logger.Log("[ExternalScalerService] Bucle de escalado externo detenido.");
        }

        private async Task ScalerLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested && _isScalingActive)
            {
                try
                {
                    var (hShared, width, height) = SteamOSSharedMemory.Instance.ReadSharedTextureInfo();

                    if (hShared != IntPtr.Zero && hShared != _lastHandle && _d3dDevice != null)
                    {
                        CleanupSharedResources();
                        _lastHandle = hShared;
                        try
                        {
                            _sharedTexture = _d3dDevice.OpenSharedResource<SharpDX.Direct3D11.Texture2D>(hShared);
                            if (_sharedTexture != null)
                            {
                                _keyedMutex = _sharedTexture.QueryInterface<KeyedMutex>();
                                Logger.Log($"[ExternalScalerService] Conectado exitosamente a la textura compartida VRAM {width}x{height} (Handle=0x{hShared.ToInt64():X}).");
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log($"[ExternalScalerService] Error al abrir textura compartida: {ex.Message}");
                        }
                    }

                    if (_sharedTexture != null && _keyedMutex != null)
                    {
                        // Sincronización KeyedMutex: Adquirir clave 1 (escrita por la DLL)
                        SharpDX.Result res = _keyedMutex.Acquire(1, 16);
                        if (res.Success)
                        {
                            try
                            {
                                // Renderizado de lectura o escalado FSR
                            }
                            finally
                            {
                                _keyedMutex.Release(0); // Devolver clave 0 a la DLL
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ExternalScalerService] Excepción en ciclo de escalado: {ex.Message}");
                    CleanupSharedResources();
                }

                await Task.Delay(16, token).ConfigureAwait(false); // ~60 FPS
            }
        }

        private void CleanupSharedResources()
        {
            _keyedMutex?.Dispose();
            _keyedMutex = null;
            _sharedTexture?.Dispose();
            _sharedTexture = null;
            _lastHandle = IntPtr.Zero;
        }

        public void Dispose()
        {
            StopScaling();
            _d3dDevice?.Dispose();
            _d3dDevice = null;
        }
    }
}
