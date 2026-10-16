#if DEV_CHEAT
using System;
using System.Globalization;
using System.Reflection;
using System.Text;
using HB.Cheat;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>
/// Play Mode 관찰용 치트. unity command cheat_run 으로 상태를 JSON 문자열로 읽는다.
/// 대상 컴포넌트의 동작(할당·스케줄·해제)은 건드리지 않는다. 예외: scene.reload는 씬을 다시 여는 조작 치트다.
/// </summary>
public static class ObservationCheats
{
    private const BindingFlags k_Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    private static FieldInfo s_Positions;
    private static FieldInfo s_Velocities;
    private static FieldInfo s_CellCounts;
    private static FieldInfo s_Handle;
    private static int s_BlockedReads;

    [Cheat("boid.status", "Observe/Boid Status", Order = 0, Description = "BoidSystem 상태를 JSON으로 돌려준다")]
    public static CheatResult BoidStatus()
    {
        var boid = UnityEngine.Object.FindAnyObjectByType<BoidSystem>();
        if (boid == null) return CheatResult.Fail("BoidSystem not found");

        if (s_Positions == null)
        {
            var type = typeof(BoidSystem);
            s_Positions = type.GetField("_positions", k_Instance);
            s_Velocities = type.GetField("_velocities", k_Instance);
            s_CellCounts = type.GetField("_cellCounts", k_Instance);
            s_Handle = type.GetField("_handle", k_Instance);
        }

        var positions = (NativeArray<float3>)s_Positions.GetValue(boid);
        var velocities = (NativeArray<float3>)s_Velocities.GetValue(boid);
        var cellCounts = (NativeArray<int>)s_CellCounts.GetValue(boid);
        var handle = (JobHandle)s_Handle.GetValue(boid);

        string cell0 = "null";
        string length = positions.IsCreated ? positions.Length.ToString(CultureInfo.InvariantCulture) : "null";
        if (cellCounts.IsCreated && cellCounts.Length > 0)
        {
            // 잡이 쓰는 중에 읽으면 Safety 예외가 난다. Console에 남기지 않고 실패로 돌려준다.
            try
            {
                cell0 = cellCounts[0].ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception e)
            {
                s_BlockedReads++;
                return CheatResult.Fail($"read blocked ({e.GetType().Name}), blockedReads={s_BlockedReads}");
            }
        }

        Vector3 p = boid.transform.position;
        var sb = new StringBuilder(256);
        sb.Append("{\"frame\":").Append(Time.frameCount)
          .Append(",\"cell0\":").Append(cell0)
          .Append(",\"positionsLength\":").Append(length)
          .Append(",\"position\":[")
          .Append(p.x.ToString("R", CultureInfo.InvariantCulture)).Append(',')
          .Append(p.y.ToString("R", CultureInfo.InvariantCulture)).Append(',')
          .Append(p.z.ToString("R", CultureInfo.InvariantCulture)).Append(']')
          .Append(",\"created\":{\"positions\":").Append(Bool(positions.IsCreated))
          .Append(",\"velocities\":").Append(Bool(velocities.IsCreated))
          .Append(",\"cellCounts\":").Append(Bool(cellCounts.IsCreated)).Append('}')
          .Append(",\"handleCompleted\":").Append(Bool(handle.IsCompleted))
          .Append(",\"blockedReads\":").Append(s_BlockedReads)
          .Append('}');
        return CheatResult.Ok(sb.ToString());
    }

    [Cheat("scene.reload", "Scene/Reload", Order = 0, Description = "활성 씬을 다시 연다")]
    public static CheatResult ReloadScene()
    {
        Scene scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
        // Build Settings에 없는 씬도 열 수 있도록 에디터에서는 경로로 연다
        UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(
            scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
        SceneManager.LoadScene(scene.buildIndex);
#endif
        return CheatResult.Ok(scene.path);
    }

    [Cheat("log.counts", "Observe/Log Counts", Order = 1, Description = "BeforeSceneLoad 이후 로그 개수를 JSON으로 돌려준다")]
    public static CheatResult LogCounts() => CheatResult.Ok(LogCounter.ToJson());

    [Cheat("log.reset", "Observe/Log Reset", Order = 2, Description = "로그 개수를 0으로 만든다")]
    public static CheatResult LogReset()
    {
        LogCounter.Reset();
        return CheatResult.Ok();
    }

    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// Application.logMessageReceivedThreaded 로 LogType별 개수와, Log가 아닌 메시지의 첫 줄별 개수를 센다.
    /// 콜백에서는 할당하지 않는다(고정 배열 + 첫 줄 해시, 문자열은 Unity가 넘긴 참조만 보관).
    /// </summary>
    private static class LogCounter
    {
        private const int k_SlotCount = 32;

        private static readonly object s_Lock = new object();
        private static readonly int[] s_TypeCounts = new int[5];
        private static readonly int[] s_SlotHashes = new int[k_SlotCount];
        private static readonly int[] s_SlotCounts = new int[k_SlotCount];
        private static readonly LogType[] s_SlotTypes = new LogType[k_SlotCount];
        private static readonly string[] s_SlotSamples = new string[k_SlotCount];
        private static int s_SlotUsed;
        private static int s_Overflow;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        private static void ResetStatics()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Reset();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void Subscribe()
        {
            Application.logMessageReceivedThreaded -= OnLog;
            Application.logMessageReceivedThreaded += OnLog;
        }

        public static void Reset()
        {
            lock (s_Lock)
            {
                Array.Clear(s_TypeCounts, 0, s_TypeCounts.Length);
                Array.Clear(s_SlotHashes, 0, k_SlotCount);
                Array.Clear(s_SlotCounts, 0, k_SlotCount);
                Array.Clear(s_SlotSamples, 0, k_SlotCount);
                s_SlotUsed = 0;
                s_Overflow = 0;
            }
        }

        private static void OnLog(string condition, string stackTrace, LogType type)
        {
            int typeIndex = (int)type;
            int hash = type == LogType.Log ? 0 : FirstLineHash(condition, type);

            lock (s_Lock)
            {
                if (typeIndex >= 0 && typeIndex < s_TypeCounts.Length) s_TypeCounts[typeIndex]++;
                if (type == LogType.Log) return;

                for (int i = 0; i < s_SlotUsed; i++)
                {
                    if (s_SlotHashes[i] == hash && s_SlotTypes[i] == type && FirstLineEquals(s_SlotSamples[i], condition))
                    {
                        s_SlotCounts[i]++;
                        return;
                    }
                }

                if (s_SlotUsed == k_SlotCount)
                {
                    s_Overflow++;
                    return;
                }

                s_SlotHashes[s_SlotUsed] = hash;
                s_SlotTypes[s_SlotUsed] = type;
                s_SlotCounts[s_SlotUsed] = 1;
                s_SlotSamples[s_SlotUsed] = condition;
                s_SlotUsed++;
            }
        }

        private static int FirstLineHash(string text, LogType type)
        {
            unchecked
            {
                int hash = (int)2166136261 ^ (int)type;
                if (text == null) return hash;
                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '\n' || c == '\r') break;
                    hash = (hash ^ c) * 16777619;
                }
                return hash;
            }
        }

        private static bool FirstLineEquals(string a, string b)
        {
            if (ReferenceEquals(a, b)) return true;
            if (a == null || b == null) return false;
            int i = 0;
            while (true)
            {
                bool endA = i >= a.Length || a[i] == '\n' || a[i] == '\r';
                bool endB = i >= b.Length || b[i] == '\n' || b[i] == '\r';
                if (endA || endB) return endA && endB;
                if (a[i] != b[i]) return false;
                i++;
            }
        }

        public static string ToJson()
        {
            var sb = new StringBuilder(1024);
            lock (s_Lock)
            {
                sb.Append("{\"frame\":").Append(Time.frameCount)
                  .Append(",\"error\":").Append(s_TypeCounts[(int)LogType.Error])
                  .Append(",\"assert\":").Append(s_TypeCounts[(int)LogType.Assert])
                  .Append(",\"warning\":").Append(s_TypeCounts[(int)LogType.Warning])
                  .Append(",\"log\":").Append(s_TypeCounts[(int)LogType.Log])
                  .Append(",\"exception\":").Append(s_TypeCounts[(int)LogType.Exception])
                  .Append(",\"overflow\":").Append(s_Overflow)
                  .Append(",\"messages\":[");
                for (int i = 0; i < s_SlotUsed; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"type\":\"").Append(s_SlotTypes[i]).Append("\",\"count\":").Append(s_SlotCounts[i])
                      .Append(",\"text\":\"");
                    AppendFirstLineEscaped(sb, s_SlotSamples[i]);
                    sb.Append("\"}");
                }
                sb.Append("]}");
            }
            return sb.ToString();
        }

        private static void AppendFirstLineEscaped(StringBuilder sb, string text)
        {
            if (text == null) return;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n' || c == '\r') break;
                if (c == '"' || c == '\\') sb.Append('\\');
                if (c < ' ') continue;
                sb.Append(c);
            }
        }
    }
}
#endif
