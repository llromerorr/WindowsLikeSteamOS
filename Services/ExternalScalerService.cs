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
        private RenderTargetView? _rtv;
        private Texture2D? _sharedTexture;
        private KeyedMutex? _keyedMutex;

        private VertexShader? _vs;
        private PixelShader? _ps;
        private SharpDX.Direct3D11.Buffer? _constantBuffer;
        private SamplerState? _samplerState;

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

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        private void RenderThreadProc()
        {
            // 1. Crear ventana Win32 pura
            IntPtr hInstance = GetModuleHandle(null);
            WNDCLASS wc = new WNDCLASS
            {
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate((WndProcDelegate)WndProc),
                hInstance = hInstance,
                lpszClassName = "SteamOS_ExternalCompositor_Class_" + Guid.NewGuid().ToString("N")
            };
            RegisterClass(ref wc);

            uint exStyle = WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT;
            _hwnd = CreateWindowEx(exStyle, wc.lpszClassName, "SteamOS External Compositor", WS_POPUP,
                0, 0, _screenWidth, _screenHeight, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

            ShowWindow(_hwnd, SW_SHOW);

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

            try
            {
                using var factory = new Factory1();
                Adapter1? targetAdapter = null;
                foreach (var adapter in factory.Adapters1)
                {
                    if (adapter.Description.Luid == adapterLuid)
                    {
                        targetAdapter = adapter;
                        break;
                    }
                }

                if (targetAdapter == null)
                    targetAdapter = factory.GetAdapter1(0);

                _d3dDevice = new Device(targetAdapter, DeviceCreationFlags.BgraSupport | DeviceCreationFlags.Debug, FeatureLevel.Level_11_0);
                
                var desc = new SwapChainDescription
                {
                    BufferCount = 2,
                    ModeDescription = new ModeDescription(_screenWidth, _screenHeight, new Rational(0, 1), Format.B8G8R8A8_UNorm), // Match format with DXGI hooks usually BGRA or RGBA
                    IsWindowed = true,
                    OutputHandle = _hwnd,
                    SampleDescription = new SampleDescription(1, 0),
                    SwapEffect = SwapEffect.FlipDiscard,
                    Usage = Usage.RenderTargetOutput
                };

                _swapChain = new SwapChain(factory, _d3dDevice, desc);

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

        private void LoadShaders()
        {
            if (_d3dDevice == null) return;
            
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                string[] resNames = assembly.GetManifestResourceNames();
                string vsName = "", psName = "";
                foreach(var n in resNames) {
                    if (n.EndsWith("CRT_VS.cso")) vsName = n;
                    if (n.EndsWith("CRT_PS.cso")) psName = n;
                }

                using (var vsStream = assembly.GetManifestResourceStream(vsName))
                using (var psStream = assembly.GetManifestResourceStream(psName))
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

                _constantBuffer = new SharpDX.Direct3D11.Buffer(_d3dDevice, 32, ResourceUsage.Dynamic, BindFlags.ConstantBuffer, CpuAccessFlags.Write, ResourceOptionFlags.None, 0);

                var sampDesc = new SamplerStateDescription
                {
                    Filter = Filter.MinMagMipLinear,
                    AddressU = TextureAddressMode.Clamp,
                    AddressV = TextureAddressMode.Clamp,
                    AddressW = TextureAddressMode.Clamp,
                    ComparisonFunction = Comparison.Never
                };
                _samplerState = new SamplerState(_d3dDevice, sampDesc);
            }
            catch (Exception ex)
            {
                Logger.Log($"[ExternalScalerService] Error cargando shaders CSO: {ex.Message}");
            }
        }

        private void RenderFrame()
        {
            var (hShared, width, height, luid) = SteamOSSharedMemory.Instance.ReadSharedTextureInfo();
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
                    _sharedTexture = _d3dDevice.OpenSharedResource<Texture2D>(hShared);
                    if (_sharedTexture != null)
                    {
                        _keyedMutex = _sharedTexture.QueryInterface<KeyedMutex>();
                        var desc = _sharedTexture.Description;
                        Logger.Log($"[ExternalScalerService] Conectado a textura compartida {width}x{height} (Handle=0x{hShared.ToInt64():X}). Formato real: {desc.Format}");
                        
                        try {
                           using var testSrv = new ShaderResourceView(_d3dDevice, _sharedTexture);
                           Logger.Log($"[ExternalScalerService] SRV creado exitosamente. Formato: {testSrv.Description.Format}");
                        } catch (Exception ex) {
                           Logger.Log($"[ExternalScalerService] Falla crítica al crear SRV: {ex.Message}");
                        }
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
                    // C++ adquiere 0, libera 1. C# adquiere 1, libera 0. Timeout 16ms para evitar cuelgues.
                    var res = _keyedMutex.Acquire(1, 16);
                    if (res == SharpDX.Result.Ok)
                    {
                        _timeoutSpamCounter = 0;
                        try
                        {
                            DrawD3DFrame();
                        }
                        finally
                        {
                            _keyedMutex.Release(0);
                        }
                    }
                    else
                    {
                        // TIMEOUT
                        _timeoutSpamCounter++;
                        if (_timeoutSpamCounter == 1 || _timeoutSpamCounter % 60 == 0)
                        {
                            Logger.Log($"[ExternalScalerService] WAIT_TIMEOUT ({_timeoutSpamCounter} veces). Reusando último frame.");
                        }
                    }

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
                catch (Exception ex)
                {
                    Logger.Log($"[ExternalScalerService] Error en ciclo de render: {ex.Message}");
                }
            }
        }

        private void DrawD3DFrame()
        {
            var ctx = _d3dDevice!.ImmediateContext;
            ctx.OutputMerger.SetRenderTargets(_rtv);
            ctx.Rasterizer.SetViewport(new SharpDX.Mathematics.Interop.RawViewportF { X = 0, Y = 0, Width = _screenWidth, Height = _screenHeight, MinDepth = 0, MaxDepth = 1 });
            
            using (var srv = new ShaderResourceView(_d3dDevice, _sharedTexture))
            {
                var p = SteamOSSharedMemory.Instance.ReadCurrentParams();
                
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
