using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using SharpDX.Direct3D;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using SteamOSConfigurator;
using Device = SharpDX.Direct3D11.Device;

namespace WindowsLikeSteamOS.Services
{
    public sealed class ExternalScalerService : IDisposable
    {
        private static readonly Lazy<ExternalScalerService> _lazyInstance = new Lazy<ExternalScalerService>(() => new ExternalScalerService());
        public static ExternalScalerService Instance => _lazyInstance.Value;

        private Thread? _renderThread;
        private bool _isRunning;
        private IntPtr _hwnd;
        private int _screenWidth;
        private int _screenHeight;

        private Device? _d3dDevice;
        private SwapChain? _swapChain;
        private IntPtr _waitableObject = IntPtr.Zero;
        private RenderTargetView? _rtv;
        private Texture2D? _sharedTexture;
        private KeyedMutex? _keyedMutex;

        private VertexShader? _vs;
        private PixelShader? _ps;
        private SharpDX.Direct3D11.Buffer? _constantBuffer;
        private SamplerState? _samplerState;
        private BlendState? _opaqueBlendState;

        private VertexShader? _fsrVs;
        private PixelShader? _fsrEasuPs;
        private PixelShader? _fsrRcasPs;
        private SharpDX.Direct3D11.Buffer? _fsrEasuBuffer;
        private SharpDX.Direct3D11.Buffer? _fsrRcasBuffer;
        
        private Texture2D? _intermediateTexture;
        private RenderTargetView? _intermediateRtv;
        private ShaderResourceView? _intermediateSrv;

        private long _lastAdapterLuid;
        private IntPtr _lastHandle = IntPtr.Zero;
        private int _timeoutSpamCounter = 0;

        // --- WIN32 IMPORTS ---
        private const uint WS_EX_TOPMOST = 0x00000008;
        private const uint WS_EX_TRANSPARENT = 0x00000020;
        private const uint WS_EX_NOACTIVATE = 0x08000000;
        private const uint WS_POPUP = 0x80000000;
        private const int SW_SHOW = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public IntPtr hwnd;
            public uint message;
            public IntPtr wParam;
            public IntPtr lParam;
            public uint time;
            public System.Drawing.Point pt;
        }

        [DllImport("user32.dll")]
        private static extern ushort RegisterClass([In] ref WNDCLASS lpWndClass);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern IntPtr DefWindowProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        private static extern bool TranslateMessage([In] ref MSG lpMsg);

        [DllImport("user32.dll")]
        private static extern IntPtr DispatchMessage([In] ref MSG lpmsg);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        // Handles que deben quedar SIEMPRE por encima del compositor FSR (panel WPF, OSD, etc.).
        // Asignar desde fuera (p.ej. desde el ViewModel/Window principal) ANTES de llamar a StartScaling().
        public static IntPtr[] OverlayHandlesToKeepOnTop = Array.Empty<IntPtr>();

        private const uint SWP_NOMOVE = 0x0002;
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOACTIVATE = 0x0010;

        private ExternalScalerService()
        {
            _screenWidth = System.Windows.SystemParameters.PrimaryScreenWidth > 0 ? (int)System.Windows.SystemParameters.PrimaryScreenWidth : 1920;
            _screenHeight = System.Windows.SystemParameters.PrimaryScreenHeight > 0 ? (int)System.Windows.SystemParameters.PrimaryScreenHeight : 1080;
        }

        public void StartScaling()
        {
            if (_isRunning) return;
            _isRunning = true;

            _renderThread = new Thread(RenderThreadProc)
            {
                IsBackground = true,
                Name = "ExternalScalerService_RenderThread"
            };
            _renderThread.SetApartmentState(ApartmentState.STA);
            _renderThread.Start();
            Logger.Log("[ExternalScalerService] Hilo de renderizado dedicado iniciado.");
        }

        public void StopScaling()
        {
            _isRunning = false;
            _renderThread?.Join(1000);
            _renderThread = null;
            Logger.Log("[ExternalScalerService] Hilo de renderizado detenido.");
        }

        private WndProcDelegate? _wndProcDelegate;

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            const uint WM_NCHITTEST = 0x0084;
            if (msg == WM_NCHITTEST)
            {
                return (IntPtr)(-1); // HTTRANSPARENT
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void RenderThreadProc()
        {
            // 1. Crear ventana Win32 pura
            IntPtr hInstance = GetModuleHandle(null);
            _wndProcDelegate = new WndProcDelegate(WndProc);
            WNDCLASS wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate),
                hInstance = hInstance,
                lpszClassName = "SteamOS_ExternalCompositor_Class_" + Guid.NewGuid().ToString("N")
            };
            RegisterClass(ref wc);

            uint exStyle = WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            _hwnd = CreateWindowEx(exStyle, wc.lpszClassName, "SteamOS External Compositor", WS_POPUP,
                0, 0, _screenWidth, _screenHeight, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            ShowWindow(_hwnd, SW_SHOW);

            // BUG FIX: al crearse con WS_EX_TOPMOST, esta ventana se inserta en la CIMA
            // de la banda "topmost", tapando cualquier ventana topmost previa (panel WPF, OSD).
            // Reafirmamos esas ventanas por encima de la nuestra inmediatamente después de mostrarla.
            IntPtr HWND_TOPMOST = new IntPtr(-1);
            foreach (var overlayHandle in OverlayHandlesToKeepOnTop)
            {
                if (overlayHandle != IntPtr.Zero)
                {
                    SetWindowPos(overlayHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }

            // Bucle principal
            while (_isRunning)
            {
                if (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, 1 /* PM_REMOVE */))
                {
                    if (msg.message == 0x0012) // WM_QUIT
                        break;
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                else
                {
                    RenderFrame();
                }
            }

            CleanupD3D();
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        private void RecreateD3D(long adapterLuid)
        {
            CleanupD3D();
            _lastAdapterLuid = adapterLuid;
            _waitableObject = IntPtr.Zero;

            try
            {
                using var factory2 = new Factory2();
                Adapter1? targetAdapter = null;
                foreach (var adapter in factory2.Adapters1)
                {
                    if (adapter.Description.Luid == adapterLuid)
                    {
                        targetAdapter = adapter;
                        break;
                    }
                }

                if (targetAdapter == null)
                    targetAdapter = factory2.GetAdapter1(0);

                DeviceCreationFlags creationFlags = DeviceCreationFlags.BgraSupport;
#if DEBUG
                creationFlags |= DeviceCreationFlags.Debug;
#endif

                try
                {
                    _d3dDevice = new Device(targetAdapter, creationFlags, FeatureLevel.Level_11_0);
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ExternalScalerService] Falló creación de D3D11 Device con flags '{creationFlags}': {ex.Message}. Reintentando con BgraSupport solamente.");
                    _d3dDevice = new Device(targetAdapter, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
                }
                
                var desc1 = new SwapChainDescription1
                {
                    Width = _screenWidth,
                    Height = _screenHeight,
                    Format = Format.B8G8R8A8_UNorm,
                    Stereo = false,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = Usage.RenderTargetOutput,
                    BufferCount = 2,
                    Scaling = Scaling.Stretch,
                    SwapEffect = SwapEffect.FlipDiscard,
                    AlphaMode = AlphaMode.Unspecified,
                    Flags = SwapChainFlags.FrameLatencyWaitAbleObject
                };

                _swapChain = new SwapChain1(factory2, _d3dDevice, _hwnd, ref desc1, null, null);

                using var swapChain2 = _swapChain.QueryInterface<SwapChain2>();
                if (swapChain2 != null)
                {
                    swapChain2.MaximumFrameLatency = 1;
                    _waitableObject = swapChain2.FrameLatencyWaitableObject;
                }

                using (var backBuffer = Texture2D.FromSwapChain<Texture2D>(_swapChain, 0))
                {
                    _rtv = new RenderTargetView(_d3dDevice, backBuffer);
                }

                LoadShaders();

                Logger.Log($"[ExternalScalerService] D3D11 inicializado correctamente en LUID: {adapterLuid}");
            }
            catch (Exception ex)
            {
                Logger.Log($"[ExternalScalerService] Error inicializando D3D11: {ex.Message}");
            }
        }

        private void UpdateIntermediateTexture(int width, int height)
        {
            if (_intermediateTexture != null && _intermediateTexture.Description.Width == width && _intermediateTexture.Description.Height == height)
                return;
            
            _intermediateSrv?.Dispose();
            _intermediateRtv?.Dispose();
            _intermediateTexture?.Dispose();
            
            var desc = new Texture2DDescription
            {
                Width = width,
                Height = height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.R8G8B8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource,
                CpuAccessFlags = CpuAccessFlags.None,
                OptionFlags = ResourceOptionFlags.None
            };
            
            _intermediateTexture = new Texture2D(_d3dDevice, desc);
            _intermediateRtv = new RenderTargetView(_d3dDevice, _intermediateTexture);
            _intermediateSrv = new ShaderResourceView(_d3dDevice, _intermediateTexture);
        }

        private void LoadShaders()
        {
            if (_d3dDevice == null) return;
            
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string[] resNames = assembly.GetManifestResourceNames();
                string crtVsName = "", crtPsName = "";
                string fsrVsName = "", fsrEasuName = "", fsrRcasName = "";
                
                foreach(var n in resNames) {
                    if (n.EndsWith("CRT_VS.cso")) crtVsName = n;
                    if (n.EndsWith("CRT_PS.cso")) crtPsName = n;
                    if (n.EndsWith("FSR_VS.cso")) fsrVsName = n;
                    if (n.EndsWith("FSR_EASU_PS.cso")) fsrEasuName = n;
                    if (n.EndsWith("FSR_RCAS_PS.cso")) fsrRcasName = n;
                }

                // CRT
                using (var vsStream = string.IsNullOrEmpty(crtVsName) ? null : assembly.GetManifestResourceStream(crtVsName))
                using (var psStream = string.IsNullOrEmpty(crtPsName) ? null : assembly.GetManifestResourceStream(crtPsName))
                {
                    if (vsStream != null && psStream != null)
                    {
                        var vsBytes = new byte[vsStream.Length];
                        vsStream.Read(vsBytes, 0, vsBytes.Length);
                        _vs = new VertexShader(_d3dDevice, vsBytes);

                        var psBytes = new byte[psStream.Length];
                        psStream.Read(psBytes, 0, psBytes.Length);
                        _ps = new PixelShader(_d3dDevice, psBytes);
                    }
                }

                // FSR
                using (var fvs = string.IsNullOrEmpty(fsrVsName) ? null : assembly.GetManifestResourceStream(fsrVsName))
                using (var easu = string.IsNullOrEmpty(fsrEasuName) ? null : assembly.GetManifestResourceStream(fsrEasuName))
                using (var rcas = string.IsNullOrEmpty(fsrRcasName) ? null : assembly.GetManifestResourceStream(fsrRcasName))
                {
                    if (fvs != null) { var b = new byte[fvs.Length]; fvs.Read(b, 0, b.Length); _fsrVs = new VertexShader(_d3dDevice, b); }
                    if (easu != null) { var b = new byte[easu.Length]; easu.Read(b, 0, b.Length); _fsrEasuPs = new PixelShader(_d3dDevice, b); }
                    if (rcas != null) { var b = new byte[rcas.Length]; rcas.Read(b, 0, b.Length); _fsrRcasPs = new PixelShader(_d3dDevice, b); }
                }

                Logger.Log($"[ExternalScalerService] Shaders cargados. CRT VS: {_vs != null}, CRT PS: {_ps != null}, FSR VS: {_fsrVs != null}, FSR EASU: {_fsrEasuPs != null}, FSR RCAS: {_fsrRcasPs != null}");


                _constantBuffer = new SharpDX.Direct3D11.Buffer(_d3dDevice, 32, ResourceUsage.Dynamic, BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
                
                // FSR Constants: EASU needs 64 bytes (4 uint4), RCAS needs 16 bytes (1 uint4)
                _fsrEasuBuffer = new SharpDX.Direct3D11.Buffer(_d3dDevice, 64, ResourceUsage.Dynamic, BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);
                _fsrRcasBuffer = new SharpDX.Direct3D11.Buffer(_d3dDevice, 16, ResourceUsage.Dynamic, BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

                var sampDesc = new SamplerStateDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    ComparisonFunction = Comparison.Never
                };
                _samplerState = new SamplerState(_d3dDevice, sampDesc);

                var blendDesc = new BlendStateDescription();
                blendDesc.RenderTarget[0].IsBlendEnabled = false;
                blendDesc.RenderTarget[0].RenderTargetWriteMask = ColorWriteMaskFlags.All;
                _opaqueBlendState = new BlendState(_d3dDevice, blendDesc);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ExternalScalerService] Error cargando shaders CSO: {ex.Message}");
            }
        }

        private int _zOrderReassertCounter = 0;

        private void ReassertOverlaysOnTop()
        {
            IntPtr HWND_TOPMOST = new IntPtr(-1);
            foreach (var overlayHandle in OverlayHandlesToKeepOnTop)
            {
                if (overlayHandle != IntPtr.Zero)
                {
                    SetWindowPos(overlayHandle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }
        }

        private void RenderFrame()
        {
            // Reafirma cada ~120 frames (2s a 60fps) que el panel/OSD sigan por encima del compositor.
            if (++_zOrderReassertCounter >= 120)
            {
                _zOrderReassertCounter = 0;
                ReassertOverlaysOnTop();
            }

            var (hShared, width, height, luid, isNtHandle) = SteamOSSharedMemory.Instance.ReadSharedTextureInfo();
            if (hShared == IntPtr.Zero)
            {
                Thread.Sleep(5);
                return;
            }

            if (_d3dDevice == null || luid != _lastAdapterLuid)
            {
                RecreateD3D(luid);
                if (_d3dDevice == null) return;
            }

            if (hShared != _lastHandle)
            {
                CleanupSharedResources();
                _lastHandle = hShared;
                try
                {
                    if (isNtHandle)
                    {
                        using (var device1 = _d3dDevice.QueryInterface<SharpDX.Direct3D11.Device1>())
                        {
                            _sharedTexture = device1.OpenSharedResource1<Texture2D>(hShared);
                        }
                    }
                    else
                    {
                        _sharedTexture = _d3dDevice.OpenSharedResource<Texture2D>(hShared);
                    }
                    
                    if (_sharedTexture != null)
                    {
                        _keyedMutex = _sharedTexture.QueryInterface<KeyedMutex>();
                        Logger.Log($"[ExternalScalerService] Conectado a textura compartida {width}x{height} (Handle=0x{hShared.ToInt64():X}, NT={isNtHandle}).");
                    }
                }
                catch (SharpDX.SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved || ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset)
                {
                    Logger.Log("[ExternalScalerService] DEVICE LOST detectado al abrir recurso. Recreando D3D11...");
                    RecreateD3D(_lastAdapterLuid);
                    return;
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ExternalScalerService] Error al abrir textura compartida: {ex.Message}");
                }
            }

            if (_d3dDevice != null && _sharedTexture != null && _keyedMutex != null && _swapChain != null)
            {
                try
                {
                    if (_waitableObject != IntPtr.Zero)
                    {
                        WaitForSingleObject(_waitableObject, 1000); // Wait up to 1s for the swapchain to be ready to accept a new frame
                    }

                    // C++ adquiere 0, libera 1. C# adquiere 1, libera 0. Timeout 16ms para evitar cuelgues.
                    var res = _keyedMutex.Acquire(1, 16);
                    if (res != SharpDX.Result.Ok)
                    {
                        // TIMEOUT
                        _timeoutSpamCounter++;
                        if (_timeoutSpamCounter == 1 || _timeoutSpamCounter % 60 == 0)
                        {
                            Logger.Log($"[ExternalScalerService] WAIT_TIMEOUT ({_timeoutSpamCounter} veces). Reusando último frame.");
                        }
                        return; // FIX: Salir inmediatamente. No tocar la textura, no renderizar, no presentar.
                    }

                    _timeoutSpamCounter = 0;
                    try
                    {
                        DrawD3DFrame();

                        // Present(1) para VSync.
                        try {
                            _swapChain.Present(1, PresentFlags.None);
                        } 
                        catch (SharpDX.SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved || ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceReset) 
                        {
                            Logger.Log("[ExternalScalerService] DEVICE LOST en Present(). Recreando D3D11...");
                            RecreateD3D(_lastAdapterLuid);
                        }
                    }
                    finally
                    {
                        _keyedMutex?.Release(0);
                    }
                }
                catch (Exception ex)
                {
                    Logger.Log($"[ExternalScalerService] Error en ciclo de render: {ex}");
                }
            }
        }

        private void DrawD3DFrame()
        {
            var ctx = _d3dDevice!.ImmediateContext;
            
            // Forzar opacidad antes de dibujar (Fase 1.3)
            if (_opaqueBlendState != null)
            {
                ctx.OutputMerger.SetBlendState(_opaqueBlendState);
            }

            var p = SteamOSSharedMemory.Instance.ReadCurrentParams();
            int sourceWidth = (int)_sharedTexture!.Description.Width;
            int sourceHeight = (int)_sharedTexture!.Description.Height;
            
            bool useFSR = p.enableFSR == 1;

            if (useFSR && _fsrVs != null && _fsrEasuPs != null && _fsrRcasPs != null)
            {
                UpdateIntermediateTexture(_screenWidth, _screenHeight);
                
                // 1. Pass 1: EASU
                ctx.OutputMerger.SetRenderTargets(_intermediateRtv);
                ctx.Rasterizer.SetViewport(new SharpDX.Mathematics.Interop.RawViewportF { X = 0, Y = 0, Width = _screenWidth, Height = _screenHeight, MinDepth = 0, MaxDepth = 1 });
                
                var texDesc = _sharedTexture.Description;
                var srvFormat = texDesc.Format;
                if (srvFormat == SharpDX.DXGI.Format.R8G8B8A8_Typeless) srvFormat = SharpDX.DXGI.Format.R8G8B8A8_UNorm;
                else if (srvFormat == SharpDX.DXGI.Format.B8G8R8A8_Typeless) srvFormat = SharpDX.DXGI.Format.B8G8R8A8_UNorm;
                else if (srvFormat == SharpDX.DXGI.Format.R10G10B10A2_Typeless) srvFormat = SharpDX.DXGI.Format.R10G10B10A2_UNorm;

                var srvDesc = new SharpDX.Direct3D11.ShaderResourceViewDescription
                {
                    Format = srvFormat,
                    Dimension = SharpDX.Direct3D.ShaderResourceViewDimension.Texture2D,
                    Texture2D = new SharpDX.Direct3D11.ShaderResourceViewDescription.Texture2DResource { MipLevels = 1, MostDetailedMip = 0 }
                };
                
                using (var srv = new ShaderResourceView(_d3dDevice, _sharedTexture, srvDesc))
                {
                    var easuCon = SteamOSConfigurator.Helpers.FsrConstants.CalculateEasu(
                        sourceWidth, sourceHeight,
                        sourceWidth, sourceHeight,
                        _screenWidth, _screenHeight);
                    
                    SharpDX.DataStream mapped;
                    ctx.MapSubresource(_fsrEasuBuffer, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out mapped);
                    mapped.Write(easuCon);
                    ctx.UnmapSubresource(_fsrEasuBuffer, 0);

                    ctx.InputAssembler.InputLayout = null;
                    ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                    ctx.VertexShader.Set(_fsrVs);
                    ctx.PixelShader.Set(_fsrEasuPs);
                    ctx.PixelShader.SetConstantBuffer(0, _fsrEasuBuffer);
                    ctx.PixelShader.SetShaderResource(0, srv);
                    ctx.PixelShader.SetSampler(0, _samplerState);

                    ctx.Draw(3, 0);
                    
                    // Unbind SRV/RTV
                    ctx.OutputMerger.SetRenderTargets((RenderTargetView?)null);
                    ctx.PixelShader.SetShaderResource(0, null);
                }

                // 2. Pass 2: RCAS
                ctx.OutputMerger.SetRenderTargets(_rtv);
                
                // Clamp sharpness between 0.0 (sharpest) and 2.0 (softest) to prevent extreme artifacts (white dots)
                float clampedSharpness = Math.Max(0.0f, Math.Min(2.0f, p.fsrSharpness));
                var rcasCon = SteamOSConfigurator.Helpers.FsrConstants.CalculateRcas(clampedSharpness);
                SharpDX.DataStream mappedRcas;
                ctx.MapSubresource(_fsrRcasBuffer, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out mappedRcas);
                mappedRcas.Write(rcasCon);
                ctx.UnmapSubresource(_fsrRcasBuffer, 0);

                ctx.VertexShader.Set(_fsrVs);
                ctx.PixelShader.Set(_fsrRcasPs);
                ctx.PixelShader.SetConstantBuffer(0, _fsrRcasBuffer);
                ctx.PixelShader.SetShaderResource(0, _intermediateSrv);
                ctx.PixelShader.SetSampler(0, null);

                ctx.Draw(3, 0);
                
                // Unbind
                ctx.OutputMerger.SetRenderTargets((RenderTargetView?)null);
                ctx.PixelShader.SetShaderResource(0, null);
            }
            else
            {
                ctx.OutputMerger.SetRenderTargets(_rtv);
                ctx.Rasterizer.SetViewport(new SharpDX.Mathematics.Interop.RawViewportF { X = 0, Y = 0, Width = _screenWidth, Height = _screenHeight, MinDepth = 0, MaxDepth = 1 });
                
                using (var srv = new ShaderResourceView(_d3dDevice, _sharedTexture))
                {
                    SharpDX.DataStream mappedResource;
                    ctx.MapSubresource(_constantBuffer, MapMode.WriteDiscard, SharpDX.Direct3D11.MapFlags.None, out mappedResource);
                    mappedResource.Write((float)_screenWidth);
                    mappedResource.Write((float)_screenHeight);
                    mappedResource.Write(p.curvature);
                    mappedResource.Write(p.scanlineIntensity);
                    mappedResource.Write(0.0f); // Time
                    mappedResource.Write((float)p.enableCRT);
                    mappedResource.Write(0.0f); // padding
                    mappedResource.Write(0.0f); // padding
                    ctx.UnmapSubresource(_constantBuffer, 0);

                    ctx.InputAssembler.InputLayout = null;
                    ctx.InputAssembler.PrimitiveTopology = PrimitiveTopology.TriangleList;
                    ctx.VertexShader.Set(_vs);
                    ctx.PixelShader.Set(_ps);
                    ctx.PixelShader.SetConstantBuffer(0, _constantBuffer);
                    ctx.PixelShader.SetShaderResource(0, srv);
                    ctx.PixelShader.SetSampler(0, _samplerState);

                    ctx.Draw(3, 0);
                    
                    ctx.OutputMerger.SetRenderTargets((RenderTargetView?)null);
                    ctx.PixelShader.SetShaderResource(0, null);
                }
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

        private void CleanupD3D()
        {
            CleanupSharedResources();
            _vs?.Dispose(); _vs = null;
            _ps?.Dispose(); _ps = null;
            _constantBuffer?.Dispose(); _constantBuffer = null;
            _samplerState?.Dispose(); _samplerState = null;
            _opaqueBlendState?.Dispose(); _opaqueBlendState = null;
            
            _fsrVs?.Dispose(); _fsrVs = null;
            _fsrEasuPs?.Dispose(); _fsrEasuPs = null;
            _fsrRcasPs?.Dispose(); _fsrRcasPs = null;
            _fsrEasuBuffer?.Dispose(); _fsrEasuBuffer = null;
            _fsrRcasBuffer?.Dispose(); _fsrRcasBuffer = null;
            
            _intermediateSrv?.Dispose(); _intermediateSrv = null;
            _intermediateRtv?.Dispose(); _intermediateRtv = null;
            _intermediateTexture?.Dispose(); _intermediateTexture = null;

            _rtv?.Dispose(); _rtv = null;
            _swapChain?.Dispose(); _swapChain = null;
            _d3dDevice?.Dispose(); _d3dDevice = null;
        }

        public void Dispose()
        {
            StopScaling();
            CleanupD3D();
        }
    }
}
