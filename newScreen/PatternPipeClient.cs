using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;

namespace NewScreen;

/// <summary>
/// osu!(LazerSR.Hook) 쪽 Named Pipe로 노트를 실시간 전송한다.
/// <para>
/// 파이프 이름은 <c>osu-lazer-sr-mod-{osu 프로세스 id}</c>다 — Hook의 <c>PipeServer.PipeName</c>과 같은 규칙.
/// </para>
/// <para>
/// 전송은 <b>전용 스레드</b>가 맡는다. 캡처 스레드에서 직접 쓰면 파이프가 잠깐 밀릴 때 캡처 주기가
/// 흔들리고, 그건 곧 타이밍 정밀도 손실이다. 큐에 넣고 바로 돌아온다.
/// </para>
/// </summary>
public sealed class PatternPipeClient : IDisposable
{
    readonly ConcurrentQueue<string> _queue = new();
    readonly AutoResetEvent _signal = new(false);
    readonly Thread _thread;
    volatile bool _running = true;

    /// <summary>현재 파이프가 연결돼 있는지(UI 표시용).</summary>
    public volatile bool Connected;

    /// <summary>연결에 실패했을 때의 마지막 사유(UI 표시용).</summary>
    public string? LastError { get; private set; }

    public PatternPipeClient()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "PatternPipe" };
        _thread.Start();
    }

    public void Send(string line)
    {
        if (!_running) return;
        _queue.Enqueue(line);
        _signal.Set();
    }

    void Run()
    {
        while (_running)
        {
            try
            {
                int? pid = FindOsuProcessId();

                if (pid == null)
                {
                    LastError = "osu! 프로세스를 찾을 수 없음";
                    _signal.WaitOne(1000);
                    continue;
                }

                string name = $"osu-lazer-sr-mod-{pid.Value}";

                // PipeOptions.Asynchronous가 반드시 필요하다.
                // 이게 없으면 동기(non-overlapped) 핸들이 되고, Windows는 그런 핸들의 I/O를 직렬화한다 —
                // 수신 스레드가 ReadFile에서 다음 줄을 기다리며 블록해 있으면 같은 핸들에 대한 WriteFile이
                // 그 뒤에 줄을 서서 함께 멈춘다. 실제로 첫 10바이트 쓰기에서 영원히 대기했다.
                // Launcher의 PipeClient도 같은 이유로 이 옵션을 쓴다(PipeClient.cs:66).
                using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
                pipe.Connect(2000);

                // osu!는 게임플레이 중 100ms마다 우리에게 브로드캐스트한다(PlayerGameplayPatch).
                // 읽어서 버리지 않으면 서버 핸들러가 우리에게 쓰다 멈춰 우리 줄을 읽지 못하게 된다.
                var drain = new Thread(() => DrainIncoming(pipe)) { IsBackground = true, Name = "PatternPipeDrain" };
                drain.Start();

                Connected = true;
                LastError = null;

                while (_running && pipe.IsConnected)
                {
                    if (_queue.TryDequeue(out var line))
                    {
                        // StreamWriter + AutoFlush를 쓰지 않는다. 그 경로는 매 줄마다 Flush()를 부르고,
                        // PipeStream.Flush()는 FlushFileBuffers로 내려가 상대가 다 읽어갈 때까지 반환하지 않는다.
                        // PipeStream.Write는 파이프 버퍼에 넣고 바로 돌아오므로 상대의 읽기 속도에 묶이지 않는다.
                        byte[] data = Encoding.UTF8.GetBytes(line + "\n");
                        pipe.Write(data, 0, data.Length);
                        continue;
                    }

                    _signal.WaitOne(200);
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
            finally
            {
                Connected = false;
            }

            // 연결이 끊겼으면 잠깐 쉬었다 재시도한다 — osu!가 아직 안 떠 있을 수도 있다.
            if (_running) Thread.Sleep(500);
        }
    }

    /// <summary>
    /// 들어오는 줄을 읽어서 버린다. 우리는 보내기만 하지만, <b>안 읽으면 상대가 막힌다.</b>
    /// 파이프가 끊기거나 dispose되면 예외가 나는데 그게 정상 종료 경로다.
    /// </summary>
    void DrainIncoming(NamedPipeClientStream pipe)
    {
        try
        {
            using var reader = new StreamReader(pipe, leaveOpen: true);

            while (_running && pipe.IsConnected && reader.ReadLine() != null)
            {
            }
        }
        catch
        {
            // 연결 종료 시의 예외는 정상이다.
        }
    }

    /// <summary>osu! 프로세스 id. lazer 실행 파일은 osu!.exe 또는 osu.exe다.</summary>
    public static int? FindOsuProcessId()
    {
        foreach (string name in new[] { "osu!", "osu" })
        {
            foreach (var proc in Process.GetProcessesByName(name))
            {
                try { return proc.Id; }
                finally { proc.Dispose(); }
            }
        }

        return null;
    }

    public void Dispose()
    {
        _running = false;
        _signal.Set();
        _thread.Join(1000);
        _signal.Dispose();
    }
}
