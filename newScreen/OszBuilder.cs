using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace NewScreen;

/// <summary>
/// <c>events.log</c> + 녹음된 프로세스 오디오(WAV)로 osu! mania 6키 <c>.osz</c>를 만든다.
/// <para>
/// 1단계 검증에서 쓰던 스크래치패드 스크립트(<c>build_osz.py</c>)의 변환 로직을 그대로 포팅했다
/// (컬럼 매핑, 사이드노트 페어링, 롱노트 판정, 중복 제거). 다른 점은 두 가지뿐이다:
/// <list type="bullet">
/// <item><c>hitMs</c>가 이미 판정선 도달 시각이라 <c>startMs</c>→<c>hitMs</c> 치환 외에 별도 보정이 필요 없다.</item>
/// <item>오디오 녹음이 캡처 시작과 정확히 같은 시각(<c>t=0</c>)에 시작하므로, 노트 시각에 인위적인
/// 리드인 오프셋을 더하지 않는다 — 그러면 오디오와 노트가 어긋난다.</item>
/// </list>
/// </para>
/// </summary>
public static class OszBuilder
{
    const int LongNoteMinPx = NoteTracker.LongNoteMinHeightPx;
    const int SidePairToleranceMs = 250;
    const int Bpm = 100;

    static readonly int[] ColX6K = { 42, 128, 213, 298, 384, 469 };
    static readonly int[] ColX4K = { 64, 192, 320, 448 };
    const int ColY = 192;

    sealed record RawEvent(int Lane, long HitMs, double Velocity, double LengthPx, string Kind);
    sealed record HitObject(long Time, int Column, bool IsLong, long EndTime);

    /// <param name="eventsLogPath">이번 캡처의 <c>_events.log</c> 경로.</param>
    /// <param name="wavBytes">캡처 구간 동안 녹음한 대상 프로세스 오디오(WAV, 이미 헤더 포함).</param>
    /// <param name="captureStart">캡처 시작 시각 — 맵 제목에 그대로 쓴다.</param>
    /// <param name="outputOszPath">만들어질 <c>.osz</c> 경로.</param>
    /// <param name="excludeSideNotes">
    /// true면 SIDE 이벤트를 아예 빼고 레인0..3만으로 4키(CircleSize=4) 맵을 만든다.
    /// 감지·전송 자체는 이 옵션과 무관하게 항상 사이드를 포함한다 — osz 생성 단계에서만 걸러낸다.
    /// </param>
    public static void Build(string eventsLogPath, byte[] wavBytes, DateTime captureStart, string outputOszPath,
        bool excludeSideNotes = false)
    {
        var events = ParseEvents(eventsLogPath);

        var normal = events.Where(e => e.Kind != "SIDE").ToList();
        var hitObjects = new List<HitObject>();
        int keyCount = excludeSideNotes ? 4 : 6;
        int[] colX = excludeSideNotes ? ColX4K : ColX6K;

        foreach (var e in normal)
        {
            int column = e.Lane + (excludeSideNotes ? 0 : 1); // 레인0..3 -> 4K:컬럼0..3 / 6K:컬럼1..4
            hitObjects.Add(ToHitObject(e.HitMs, column, e.Velocity, e.LengthPx));
        }

        if (!excludeSideNotes)
        {
            var sideLeft = events.Where(e => e.Lane is 0 or 1 && e.Kind == "SIDE").OrderBy(e => e.HitMs).ToList();
            var sideRight = events.Where(e => e.Lane is 2 or 3 && e.Kind == "SIDE").OrderBy(e => e.HitMs).ToList();

            foreach (var (start, vel, length) in PairSides(sideLeft))
                hitObjects.Add(ToHitObject(start, 0, vel, length));

            foreach (var (start, vel, length) in PairSides(sideRight))
                hitObjects.Add(ToHitObject(start, 5, vel, length));
        }

        hitObjects = Dedup(hitObjects);

        string title = captureStart.ToString("yyyy-MM-dd_HH-mm-ss");
        string osuText = BuildOsuText(title, hitObjects, keyCount, colX);

        Directory.CreateDirectory(Path.GetDirectoryName(outputOszPath)!);
        using var fs = new FileStream(outputOszPath, FileMode.Create);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);

        var osuEntry = zip.CreateEntry($"{title}.osu");
        using (var w = new StreamWriter(osuEntry.Open(), Encoding.UTF8)) w.Write(osuText);

        var audioEntry = zip.CreateEntry("audio.wav");
        using (var s = audioEntry.Open()) s.Write(wavBytes, 0, wavBytes.Length);
    }

    static HitObject ToHitObject(long startMs, int column, double vel, double lengthPx)
    {
        bool isLong = lengthPx >= LongNoteMinPx;
        double holdDurationMs = vel > 0 ? lengthPx / vel : 0.0;
        long endTime = (long)Math.Round(startMs + holdDurationMs);
        return new HitObject(startMs, column, isLong, endTime);
    }

    static List<RawEvent> ParseEvents(string path)
    {
        var list = new List<RawEvent>();
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd('\r', '\n');
            if (line.Length == 0 || line.StartsWith("#")) continue;

            var cols = line.Split('\t');
            // 옛 포맷(끝시각 포함, 6열) 호환 — 있어도 무시하고 hitMs/vel/length/kind만 쓴다.
            int lane, hitMsIdx, velIdx, lenIdx, kindIdx;
            if (cols.Length == 6) { lane = 0; hitMsIdx = 1; velIdx = 3; lenIdx = 4; kindIdx = 5; }
            else { lane = 0; hitMsIdx = 1; velIdx = 2; lenIdx = 3; kindIdx = 4; }

            list.Add(new RawEvent(
                int.Parse(cols[lane], CultureInfo.InvariantCulture),
                long.Parse(cols[hitMsIdx], CultureInfo.InvariantCulture),
                double.Parse(cols[velIdx], CultureInfo.InvariantCulture),
                double.Parse(cols[lenIdx], CultureInfo.InvariantCulture),
                cols[kindIdx]));
        }
        return list;
    }

    /// <summary>레인 쌍(0&amp;1 또는 2&amp;3)의 SIDE 이벤트를 시작시각이 가까운 것끼리 묶어 하나의 노트로 병합.</summary>
    static List<(long Start, double Vel, double LengthPx)> PairSides(List<RawEvent> evs)
    {
        var byLane = evs.GroupBy(e => e.Lane).ToDictionary(g => g.Key, g => g.ToList());
        var lanes = byLane.Keys.OrderBy(x => x).ToList();

        if (lanes.Count < 2)
            return evs.Select(e => (e.HitMs, e.Velocity, e.LengthPx)).ToList();

        var aList = byLane[lanes[0]];
        var bList = byLane[lanes[1]];
        var usedB = new bool[bList.Count];
        var merged = new List<(long, double, double)>();

        foreach (var a in aList)
        {
            int bestJ = -1;
            long bestDiff = long.MaxValue;
            for (int j = 0; j < bList.Count; j++)
            {
                if (usedB[j]) continue;
                long diff = Math.Abs(a.HitMs - bList[j].HitMs);
                if (diff <= SidePairToleranceMs && diff < bestDiff) { bestDiff = diff; bestJ = j; }
            }

            if (bestJ >= 0)
            {
                var b = bList[bestJ];
                usedB[bestJ] = true;
                long start = Math.Min(a.HitMs, b.HitMs);
                double vel = a.LengthPx >= b.LengthPx ? a.Velocity : b.Velocity;
                double length = Math.Max(a.LengthPx, b.LengthPx);
                merged.Add((start, vel, length));
            }
            else
            {
                merged.Add((a.HitMs, a.Velocity, a.LengthPx));
            }
        }

        for (int j = 0; j < bList.Count; j++)
            if (!usedB[j]) merged.Add((bList[j].HitMs, bList[j].Velocity, bList[j].LengthPx));

        return merged;
    }

    /// <summary>같은 시각/컬럼에 중복으로 잡힌 노트를 하나로 합친다(롱노트 쪽 우선).</summary>
    static List<HitObject> Dedup(List<HitObject> objs)
    {
        var byKey = new Dictionary<(long, int), HitObject>();
        foreach (var o in objs)
        {
            var key = (o.Time, o.Column);
            if (byKey.TryGetValue(key, out var prev))
            {
                if (o.IsLong && (!prev.IsLong || o.EndTime > prev.EndTime)) byKey[key] = o;
            }
            else byKey[key] = o;
        }
        return byKey.Values.OrderBy(o => o.Time).ThenBy(o => o.Column).ToList();
    }

    static string BuildOsuText(string title, List<HitObject> hitObjects, int keyCount, int[] colX)
    {
        int beatLength = 60000 / Bpm;
        var sb = new StringBuilder();

        sb.AppendLine("osu file format v14");
        sb.AppendLine();
        sb.AppendLine("[General]");
        sb.AppendLine("AudioFilename: audio.wav");
        sb.AppendLine("AudioLeadIn: 0");
        sb.AppendLine("PreviewTime: -1");
        sb.AppendLine("Countdown: 0");
        sb.AppendLine("SampleSet: Normal");
        sb.AppendLine("StackLeniency: 0.7");
        sb.AppendLine("Mode: 3");
        sb.AppendLine("LetterboxInBreaks: 0");
        sb.AppendLine("SpecialStyle: 0");
        sb.AppendLine("WidescreenStoryboard: 0");
        sb.AppendLine();
        sb.AppendLine("[Editor]");
        sb.AppendLine("DistanceSpacing: 1.5");
        sb.AppendLine("BeatDivisor: 8");
        sb.AppendLine("GridSize: 4");
        sb.AppendLine("TimelineZoom: 2");
        sb.AppendLine();
        sb.AppendLine("[Metadata]");
        sb.AppendLine($"Title:{title}");
        sb.AppendLine($"TitleUnicode:{title}");
        sb.AppendLine("Artist:NewScreen");
        sb.AppendLine("ArtistUnicode:NewScreen");
        sb.AppendLine("Creator:NewScreen Auto");
        sb.AppendLine("Version:Captured Pattern (Auto)");
        sb.AppendLine("Source:");
        sb.AppendLine("Tags:newscreen capture autogenerated");
        sb.AppendLine("BeatmapID:0");
        sb.AppendLine("BeatmapSetID:-1");
        sb.AppendLine();
        sb.AppendLine("[Difficulty]");
        sb.AppendLine("HPDrainRate:5");
        sb.AppendLine($"CircleSize:{keyCount}");
        sb.AppendLine("OverallDifficulty:5");
        sb.AppendLine("ApproachRate:5");
        sb.AppendLine("SliderMultiplier:1.4");
        sb.AppendLine("SliderTickRate:1");
        sb.AppendLine();
        sb.AppendLine("[Events]");
        sb.AppendLine("//Background and Video events");
        sb.AppendLine("//Break Periods");
        sb.AppendLine("//Storyboard Layer 0 (Background)");
        sb.AppendLine("//Storyboard Layer 1 (Fail)");
        sb.AppendLine("//Storyboard Layer 2 (Pass)");
        sb.AppendLine("//Storyboard Layer 3 (Foreground)");
        sb.AppendLine("//Storyboard Layer 4 (Overlay)");
        sb.AppendLine("//Storyboard Sound Samples");
        sb.AppendLine();
        sb.AppendLine("[TimingPoints]");
        sb.AppendLine(FormattableString.Invariant($"0,{beatLength},4,2,1,100,1,0"));
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine("[HitObjects]");

        foreach (var o in hitObjects)
        {
            int x = colX[o.Column];
            sb.AppendLine(o.IsLong
                ? FormattableString.Invariant($"{x},{ColY},{o.Time},128,0,{o.EndTime}:0:0:0:0:")
                : FormattableString.Invariant($"{x},{ColY},{o.Time},1,0,0:0:0:0:"));
        }

        return sb.ToString();
    }
}
