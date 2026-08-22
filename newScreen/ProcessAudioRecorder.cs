using System.Runtime.InteropServices;

namespace NewScreen;

/// <summary>
/// 특정 프로세스가 재생하는 소리만 녹음한다(시스템 전체 루프백이 아니라 <b>프로세스별</b> 루프백).
/// <para>
/// Windows 10 2004(19041)부터 있는 "Process Loopback" WASAPI 확장(<c>ActivateAudioInterfaceAsync</c> +
/// <c>AUDIOCLIENT_ACTIVATION_PARAMS</c>)을 쓴다. 가상 장치 <c>VAD\Process_Loopback</c>을 대상 PID로
/// 활성화하면, 그 프로세스(및 자식 프로세스 트리)가 내는 소리만 따로 딸 수 있다 — 일반 WASAPI 루프백은
/// 시스템 전체 사운드가 섞인다.
/// </para>
/// <para>
/// 포맷은 협상하지 않고 PCM 16bit/44100Hz/스테레오로 고정한다(가상 루프백 장치는 <c>GetMixFormat</c>이
/// 의미 있는 값을 안 줄 수 있어서, 실측 대신 흔한 표준값을 그대로 요청한다).
/// </para>
/// </summary>
public sealed class ProcessAudioRecorder : IDisposable
{
    const int Channels = 2;
    const int SampleRate = 44100;
    const int BitsPerSample = 16;
    const int BlockAlign = Channels * BitsPerSample / 8;
    const int AvgBytesPerSec = SampleRate * BlockAlign;

    readonly uint _targetProcessId;
    IAudioClient? _audioClient;
    IAudioCaptureClient? _captureClient;
    AutoResetEvent? _dataEvent;
    ManualResetEvent? _stopEvent;
    Thread? _thread;
    MemoryStream? _pcm;

    public ProcessAudioRecorder(uint targetProcessId) => _targetProcessId = targetProcessId;

    /// <summary>대상 프로세스 오디오만 활성화하고 캡처 스레드를 띄운다. 실패하면 예외를 던진다.</summary>
    public void Start()
    {
        var activationParams = new AudioClientActivationParams
        {
            ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK,
            TargetProcessId = (int)_targetProcessId,
            ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE,
        };

        IntPtr paramsPtr = Marshal.AllocHGlobal(Marshal.SizeOf<AudioClientActivationParams>());
        IntPtr propVariantPtr = Marshal.AllocHGlobal(Marshal.SizeOf<PropVariantBlob>());
        try
        {
            Marshal.StructureToPtr(activationParams, paramsPtr, false);
            var pv = new PropVariantBlob
            {
                vt = VT_BLOB,
                blobSize = (uint)Marshal.SizeOf<AudioClientActivationParams>(),
                blobData = paramsPtr,
            };
            Marshal.StructureToPtr(pv, propVariantPtr, false);

            var handler = new ActivationCompletionHandler();
            Guid iidAudioClient = typeof(IAudioClient).GUID;

            int hr = ActivateAudioInterfaceAsync(
                VirtualAudioDeviceProcessLoopback, ref iidAudioClient, propVariantPtr, handler, out var operation);
            Marshal.ThrowExceptionForHR(hr);

            if (!handler.Done.Wait(5000))
                throw new TimeoutException("프로세스 오디오 활성화 응답이 없습니다.");

            operation.GetActivateResult(out int activateHr, out object activated);
            Marshal.ThrowExceptionForHR(activateHr);
            _audioClient = (IAudioClient)activated;
        }
        finally
        {
            Marshal.FreeHGlobal(paramsPtr);
            Marshal.FreeHGlobal(propVariantPtr);
        }

        IntPtr formatPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WaveFormatEx>());
        try
        {
            var format = new WaveFormatEx
            {
                wFormatTag = WAVE_FORMAT_PCM,
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                nAvgBytesPerSec = AvgBytesPerSec,
                nBlockAlign = (ushort)BlockAlign,
                wBitsPerSample = BitsPerSample,
                cbSize = 0,
            };
            Marshal.StructureToPtr(format, formatPtr, false);

            // hnsBufferDuration=2,000,000(=200ms), hnsPeriodicity=0(공유모드는 반드시 0).
            _audioClient.Initialize(AUDCLNT_SHAREMODE_SHARED,
                AUDCLNT_STREAMFLAGS_LOOPBACK | AUDCLNT_STREAMFLAGS_EVENTCALLBACK,
                2_000_000, 0, formatPtr, IntPtr.Zero);
        }
        finally
        {
            Marshal.FreeHGlobal(formatPtr);
        }

        _dataEvent = new AutoResetEvent(false);
        _audioClient.SetEventHandle(_dataEvent.SafeWaitHandle.DangerousGetHandle());

        Guid iidCaptureClient = typeof(IAudioCaptureClient).GUID;
        _audioClient.GetService(ref iidCaptureClient, out IntPtr captureClientPtr);
        _captureClient = (IAudioCaptureClient)Marshal.GetObjectForIUnknown(captureClientPtr);
        Marshal.Release(captureClientPtr);

        _pcm = new MemoryStream();
        _stopEvent = new ManualResetEvent(false);

        _audioClient.Start();

        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "ProcessAudioRecorder" };
        _thread.Start();
    }

    void CaptureLoop()
    {
        var handles = new WaitHandle[] { _dataEvent!, _stopEvent! };
        var silence = new byte[BlockAlign * 4096];

        while (true)
        {
            int signaled = WaitHandle.WaitAny(handles, 2000);
            if (signaled == 1) break;   // 정지 요청
            if (signaled == WaitHandle.WaitTimeout) continue;

            while (true)
            {
                _captureClient!.GetNextPacketSize(out uint packetFrames);
                if (packetFrames == 0) break;

                _captureClient.GetBuffer(out IntPtr data, out uint numFrames, out uint flags, out _, out _);

                int byteCount = (int)numFrames * BlockAlign;
                if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) != 0)
                {
                    if (silence.Length < byteCount) silence = new byte[byteCount];
                    _pcm!.Write(silence, 0, byteCount);
                }
                else
                {
                    var buf = new byte[byteCount];
                    Marshal.Copy(data, buf, 0, byteCount);
                    _pcm!.Write(buf, 0, byteCount);
                }

                _captureClient.ReleaseBuffer(numFrames);
            }
        }
    }

    /// <summary>캡처를 멈추고 지금까지 녹음한 소리를 WAV 바이트로 돌려준다. 시작에 실패했으면 null.</summary>
    public byte[]? Stop()
    {
        if (_audioClient == null) return null;

        _stopEvent?.Set();
        _thread?.Join(2000);
        _thread = null;

        try { _audioClient.Stop(); } catch { /* 이미 멈춘 상태 등은 무시 */ }

        byte[] pcmBytes = _pcm?.ToArray() ?? Array.Empty<byte>();
        ReleaseComObjects();
        return BuildWav(pcmBytes);
    }

    static byte[] BuildWav(byte[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + pcm.Length);
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)WAVE_FORMAT_PCM);
        w.Write((short)Channels);
        w.Write(SampleRate);
        w.Write(AvgBytesPerSec);
        w.Write((short)BlockAlign);
        w.Write((short)BitsPerSample);

        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(pcm.Length);
        w.Write(pcm);

        return ms.ToArray();
    }

    void ReleaseComObjects()
    {
        if (_captureClient != null) { Marshal.ReleaseComObject(_captureClient); _captureClient = null; }
        if (_audioClient != null) { Marshal.ReleaseComObject(_audioClient); _audioClient = null; }
        _dataEvent?.Dispose(); _dataEvent = null;
        _stopEvent?.Dispose(); _stopEvent = null;
        _pcm?.Dispose(); _pcm = null;
    }

    public void Dispose()
    {
        if (_audioClient != null) Stop();
        else ReleaseComObjects();
    }

    // ---- WASAPI 프로세스 루프백 상호운용 ----

    const string VirtualAudioDeviceProcessLoopback = "VAD\\Process_Loopback";

    const int AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK = 1;
    const int PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE = 0;
    const ushort VT_BLOB = 65;
    const int AUDCLNT_SHAREMODE_SHARED = 0;
    const int AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    const ushort WAVE_FORMAT_PCM = 1;

    [StructLayout(LayoutKind.Sequential)]
    struct AudioClientActivationParams
    {
        public int ActivationType;
        public int TargetProcessId;
        public int ProcessLoopbackMode;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariantBlob
    {
        public ushort vt;
        public ushort reserved1, reserved2, reserved3;
        public uint blobSize;
        public IntPtr blobData;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [DllImport("Mmdevapi.dll")]
    static extern int ActivateAudioInterfaceAsync(
        [MarshalAs(UnmanagedType.LPWStr)] string deviceInterfacePath,
        ref Guid riid,
        IntPtr activationParams,
        IActivateAudioInterfaceCompletionHandler completionHandler,
        out IActivateAudioInterfaceAsyncOperation activationOperation);

    [ComImport, Guid("41D949AB-9862-444A-80F6-C261334DA5EB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceCompletionHandler
    {
        void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation);
    }

    [ComImport, Guid("72A22D78-CDE4-431D-B8CC-843A71199B6D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IActivateAudioInterfaceAsyncOperation
    {
        void GetActivateResult(out int activateResult, [MarshalAs(UnmanagedType.IUnknown)] out object activatedInterface);
    }

    /// <summary>네이티브가 콜백하는 완료 핸들러. 신호만 세우고 나머지는 호출한 스레드가 이어서 처리한다.</summary>
    [ComVisible(true)]
    sealed class ActivationCompletionHandler : IActivateAudioInterfaceCompletionHandler
    {
        public readonly ManualResetEventSlim Done = new(false);
        public void ActivateCompleted(IActivateAudioInterfaceAsyncOperation activateOperation) => Done.Set();
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioClient
    {
        void Initialize(int shareMode, int streamFlags, long hnsBufferDuration, long hnsPeriodicity, IntPtr pFormat, IntPtr audioSessionGuid);
        void GetBufferSize(out uint numBufferFrames);
        void GetStreamLatency(out long latency);
        void GetCurrentPadding(out uint numPaddingFrames);
        void IsFormatSupported(int shareMode, IntPtr pFormat, out IntPtr closestMatch);
        void GetMixFormat(out IntPtr deviceFormat);
        void GetDevicePeriod(out long defaultDevicePeriod, out long minimumDevicePeriod);
        void Start();
        void Stop();
        void Reset();
        void SetEventHandle(IntPtr eventHandle);
        void GetService(ref Guid riid, out IntPtr ppv);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioCaptureClient
    {
        void GetBuffer(out IntPtr data, out uint numFramesToRead, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        void ReleaseBuffer(uint numFramesRead);
        void GetNextPacketSize(out uint numFramesInNextPacket);
    }
}
