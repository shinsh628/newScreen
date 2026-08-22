using System.Runtime.InteropServices;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace NewScreen;

/// <summary>
/// Windows.Graphics.Capture(WGC)로 <b>특정 창</b>의 한 영역을 읽는다. OBS의 "창 캡처(Windows 10)"와 같은 방식.
/// <para>
/// <c>CopyFromScreen</c>을 대체한다. 합성된 데스크톱을 읽는 방식은 osu! 오버레이가 대상 게임을 덮는 순간
/// osu! 픽셀만 읽히므로 구조적으로 못 쓴다. WGC는 <b>가려진 창도</b> DXGI 기반으로 직접 읽는다.
/// </para>
/// <para>
/// 좌표계: 생성자에 넘기는 영역은 <b>대상 창의 클라이언트 좌표</b>다. 화면 절대좌표가 아니므로
/// 창이 움직여도 레인 위치를 다시 잡을 필요가 없다.
/// </para>
/// </summary>
public sealed class WindowCapture : IDisposable
{
    /// <summary>매핑된 프레임을 그 자리에서 읽는다. BGRA 32bpp, 행 간격은 <paramref name="stride"/>.</summary>
    /// <param name="frameTimeMs">첫 프레임을 0으로 하는 경과 시각. 프레임이 <b>실제로 합성된</b> 시각이라
    /// 우리가 언제 요청했는지와 무관하다.</param>
    public delegate void FrameHandler(IntPtr scan0, int stride, double frameTimeMs);

    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly ID3D11Texture2D staging;
    private readonly GraphicsCaptureItem item;
    private readonly Direct3D11CaptureFramePool framePool;
    private readonly GraphicsCaptureSession session;

    private readonly int srcLeft, srcTop, width, height;

    private TimeSpan? firstFrameTime;
    private bool disposed;

    /// <summary>클라이언트 좌표를 캡처 프레임 좌표로 옮기는 오프셋. 진단용으로 로그에 남긴다.</summary>
    public int ClientOffsetX { get; }

    /// <inheritdoc cref="ClientOffsetX"/>
    public int ClientOffsetY { get; }

    /// <summary>노란 캡처 테두리를 실제로 껐는지. Windows 11(22000+)에서만 가능하다.</summary>
    public bool BorderHidden { get; }

    /// <param name="hwnd">캡처할 창.</param>
    /// <param name="clientLeft">읽을 영역의 좌측 x (대상 창 클라이언트 좌표).</param>
    /// <param name="clientTop">읽을 영역의 상단 y (대상 창 클라이언트 좌표).</param>
    public WindowCapture(IntPtr hwnd, int clientLeft, int clientTop, int width, int height)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new NotSupportedException("이 시스템에서는 WGC(창 캡처)를 쓸 수 없다. Windows 10 2004(19041) 이상이 필요하다.");

        if (width <= 0 || height <= 0)
            throw new ArgumentException("캡처 영역 크기가 0 이하다.");

        this.width = width;
        this.height = height;

        // WGC 표면의 원점은 클라이언트 원점이 아니라 DWM 확장 프레임 경계다 (테두리 없는 창이면 동일).
        var offset = Native.GetClientOffsetInCaptureFrame(hwnd);
        ClientOffsetX = offset.X;
        ClientOffsetY = offset.Y;
        srcLeft = clientLeft + ClientOffsetX;
        srcTop = clientTop + ClientOffsetY;

        // BgraSupport는 WinRT 상호운용(IDirect3DDevice)에 필수다.
        D3D11.D3D11CreateDevice(
            null,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 },
            out device!,
            out context!).CheckError();

        item = CreateItemForWindow(hwnd);

        using (var dxgiDevice = device.QueryInterface<IDXGIDevice>())
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                CreateDirect3DDevice(dxgiDevice),
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                item.Size);

        staging = device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        });

        session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = false;

        // 노란 테두리 제거는 Windows 11(22000+) 전용이다. 구형에서는 조용히 테두리를 감수한다.
        try
        {
            session.IsBorderRequired = false;
            BorderHidden = true;
        }
        catch
        {
            BorderHidden = false;
        }

        session.StartCapture();
    }

    /// <summary>
    /// 큐에 쌓인 프레임 중 <b>가장 최신 것</b>만 처리한다. 지연된 프레임으로 판정하면 타이밍이 어긋나므로
    /// 오래된 프레임은 버린다. 아직 프레임이 없으면 false.
    /// </summary>
    public bool TryProcessLatestFrame(FrameHandler handler)
    {
        if (disposed)
            return false;

        Direct3D11CaptureFrame? frame = framePool.TryGetNextFrame();

        if (frame == null)
            return false;

        // 뒤에 더 있으면 앞의 것은 낡은 프레임이다.
        while (true)
        {
            var next = framePool.TryGetNextFrame();

            if (next == null)
                break;

            frame.Dispose();
            frame = next;
        }

        try
        {
            firstFrameTime ??= frame.SystemRelativeTime;
            double frameTimeMs = (frame.SystemRelativeTime - firstFrameTime.Value).TotalMilliseconds;

            using var source = GetTexture(frame.Surface);

            var box = new Vortice.Mathematics.Box(srcLeft, srcTop, 0, srcLeft + width, srcTop + height, 1);
            context.CopySubresourceRegion(staging, 0, 0, 0, 0, source, 0, box);

            var map = context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

            try
            {
                handler(map.DataPointer, (int)map.RowPitch, frameTimeMs);
            }
            finally
            {
                context.Unmap(staging, 0);
            }

            return true;
        }
        finally
        {
            frame.Dispose();
        }
    }

    // ---- WinRT 상호운용 ----

    private static readonly Guid graphics_capture_item_iid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// <see cref="GraphicsCaptureItem"/>은 HWND로 직접 만들 수 없고, 활성화 팩토리의
    /// <c>IGraphicsCaptureItemInterop</c>을 거쳐야 한다 (WinRT 표준 상호운용 경로).
    /// </summary>
    private static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var interop = factory.AsInterface<IGraphicsCaptureItemInterop>();

        Guid iid = graphics_capture_item_iid;
        IntPtr abi = interop.CreateForWindow(hwnd, ref iid);

        try
        {
            return GraphicsCaptureItem.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>D3D11 디바이스를 WinRT가 받는 <see cref="IDirect3DDevice"/>로 감싼다.</summary>
    private static IDirect3DDevice CreateDirect3DDevice(IDXGIDevice dxgiDevice)
    {
        Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out IntPtr abi));

        try
        {
            return MarshalInspectable<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    /// <summary>WinRT 표면에서 실제 D3D11 텍스처를 꺼낸다.</summary>
    private static ID3D11Texture2D GetTexture(IDirect3DSurface surface)
    {
        var access = surface.As<IDirect3DDxgiInterfaceAccess>();
        Guid iid = typeof(ID3D11Texture2D).GUID;
        access.GetInterface(ref iid, out IntPtr abi);

        return new ID3D11Texture2D(abi);
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", SetLastError = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow(IntPtr window, ref Guid iid);
        IntPtr CreateForMonitor(IntPtr monitor, ref Guid iid);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDirect3DDxgiInterfaceAccess
    {
        void GetInterface(ref Guid iid, out IntPtr o);
    }

    public void Dispose()
    {
        if (disposed)
            return;

        disposed = true;

        session?.Dispose();
        framePool?.Dispose();
        staging?.Dispose();
        context?.Dispose();
        device?.Dispose();
    }
}
