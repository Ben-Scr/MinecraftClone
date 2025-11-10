using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Mathematics;


[Serializable]
[StructLayout(LayoutKind.Sequential)]
public struct Pcg32
{
    private ulong _state;
    private ulong _inc;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Pcg32(ulong seed, ulong seq = 0x853C49E6748FEA9BUL)
    {
        _state = 0UL;
        _inc = (seq << 1) | 1UL;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextUInt()
    {
        ulong old = _state;
        _state = unchecked(old * 6364136223846793005UL + _inc);
        uint xorshifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorshifted >> rot) | (xorshifted << ((-rot) & 31));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint NextUInt(uint bound)
    {
        if (bound == 0u) return 0u;
        uint threshold = (uint)((0u - bound) % bound);
        uint r;
        do { r = NextUInt(); } while (r < threshold);
        return r % bound;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int NextInt(int minInclusive, int maxExclusive)
    {
        int span = maxExclusive - minInclusive;
        uint u = NextUInt((uint)span);
        return (int)u + minInclusive;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextFloat()
    {
        return (NextUInt() >> 8) * (1.0f / 16777216f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextFloat01Closed()
    {
        uint u = (NextUInt() >> 8) + 1u;
        return u * (1.0f / 16777216f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float NextFloat(float min, float max) => min + (max - min) * NextFloat();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool NextBool() => (NextUInt() & 1u) != 0u;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float2 NextFloat2() => new float2(NextFloat(), NextFloat());
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float3 NextFloat3() => new float3(NextFloat(), NextFloat(), NextFloat());
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float4 NextFloat4() => new float4(NextFloat(), NextFloat(), NextFloat(), NextFloat());

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float2 NextFloat2(float2 min, float2 max) => min + (max - min) * NextFloat2();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float3 NextFloat3(float3 min, float3 max) => min + (max - min) * NextFloat3();
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float4 NextFloat4(float4 min, float4 max) => min + (max - min) * NextFloat4();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float3 NextUnitVector()
    {
        float z = NextFloat() * 2f - 1f;
        float a = NextFloat() * 2f * math.PI;
        float r = math.sqrt(math.max(0f, 1f - z * z));
        return new float3(r * math.cos(a), r * math.sin(a), z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public quaternion NextQuaternion()
    {
        float u1 = NextFloat();
        float u2 = NextFloat() * 2f * math.PI;
        float u3 = NextFloat() * 2f * math.PI;

        float s1 = math.sqrt(1f - u1);
        float s2 = math.sqrt(u1);

        float4 q = new float4(
            s1 * math.sin(u2),
            s1 * math.cos(u2),
            s2 * math.sin(u3),
            s2 * math.cos(u3)
        );
        return math.normalize(new quaternion(q.x, q.y, q.z, q.w));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public float3 NextInsideUnitSphere()
    {
        float3 dir = NextUnitVector();
        float r = math.pow(NextFloat(), 1f / 3f);
        return dir * r;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Advance(int steps)
    {
        for (int i = 0; i < steps; i++) _ = NextUInt();
    }
}
