using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;

public class BoidSystem : MonoBehaviour
{
    [SerializeField] private int count = 20000;

    private NativeArray<float3> _positions;
    private NativeArray<float3> _velocities;
    private NativeArray<int> _cellCounts;
    private JobHandle _handle;

    void Start()
    {
        _positions  = new NativeArray<float3>(count, Allocator.TempJob);
        _velocities = new NativeArray<float3>(count, Allocator.TempJob);
        _cellCounts = new NativeArray<int>(1024, Allocator.Persistent);

        for (int i = 0; i < count; i++)
            _positions[i] = UnityEngine.Random.insideUnitSphere * 50f;
    }

    [BurstCompile]
    struct CountJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<float3> positions;
        public NativeArray<int> cellCounts;

        public void Execute(int i)
        {
            int cell = (int)(math.abs(positions[i].x) % 1024);
            cellCounts[cell]++;
        }
    }

    [BurstCompile]
    struct MoveJob : IJobParallelFor
    {
        public NativeArray<float3> positions;
        [ReadOnly] public NativeArray<float3> velocities;
        public float dt;

        public void Execute(int i)
        {
            positions[i] += velocities[i] * dt;
        }
    }

    void Update()
    {
        var countJob = new CountJob { positions = _positions, cellCounts = _cellCounts };
        var h1 = countJob.Schedule(_positions.Length, 64);

        var moveJob = new MoveJob
        {
            positions = _positions,
            velocities = _velocities,
            dt = Time.deltaTime
        };
        _handle = moveJob.Schedule(_positions.Length, 64, h1);
    }

    void LateUpdate()
    {
        _handle.Complete();
        transform.position = _positions[0];
        Debug.Log($"cell0={_cellCounts[0]}");
    }

    void OnDestroy()
    {
        _positions.Dispose();
        _velocities.Dispose();
        _cellCounts.Dispose();
    }
}
