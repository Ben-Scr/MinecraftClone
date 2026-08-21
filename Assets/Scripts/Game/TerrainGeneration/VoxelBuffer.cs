using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace BenScr.MinecraftClone
{
    public sealed class VoxelBuffer<T>
    {
        private const int MaxPooledArrays = 8;
        private sealed class ArrayPoolBucket
        {
            public readonly ConcurrentBag<T[]> Arrays = new ConcurrentBag<T[]>();
            public int Count;
        }

        // Block halos and lighting maps use very different array sizes. Keeping a
        // bucket per size prevents one buffer shape from continuously evicting the
        // other and allocating again on the next mesh request.
        private static readonly ConcurrentDictionary<int, ArrayPoolBucket> ArrayPools =
            new ConcurrentDictionary<int, ArrayPoolBucket>();

        public readonly int Width;
        public readonly int Height;
        public readonly int Depth;
        public readonly int SliceStride;
        public readonly T[] Data;
        private readonly bool isPooled;
        private int hasBeenReturned;

        public VoxelBuffer(int width, int height, int depth)
        {
            Width = width;
            Height = height;
            Depth = depth;
            SliceStride = width * height;
            Data = new T[width * height * depth];
        }

        public VoxelBuffer(int width, int height, int depth, T[] data)
        {
            int expectedLength = width * height * depth;
            if (data == null || data.Length != expectedLength)
                throw new ArgumentException($"Voxel buffer data must contain exactly {expectedLength} entries.", nameof(data));

            Width = width;
            Height = height;
            Depth = depth;
            SliceStride = width * height;
            Data = data;
        }

        private VoxelBuffer(int width, int height, int depth, T[] data, bool isPooled)
        {
            Width = width;
            Height = height;
            Depth = depth;
            SliceStride = width * height;
            Data = data;
            this.isPooled = isPooled;
        }

        internal static VoxelBuffer<T> Rent(int width, int height, int depth)
        {
            int requiredLength = width * height * depth;
            ArrayPoolBucket bucket = ArrayPools.GetOrAdd(requiredLength, _ => new ArrayPoolBucket());
            if (bucket.Arrays.TryTake(out T[] data))
            {
                Interlocked.Decrement(ref bucket.Count);
                return new VoxelBuffer<T>(width, height, depth, data, isPooled: true);
            }

            return new VoxelBuffer<T>(width, height, depth, new T[requiredLength], isPooled: true);
        }

        internal void ReturnToPool()
        {
            if (!isPooled || Interlocked.Exchange(ref hasBeenReturned, 1) != 0)
                return;

            ArrayPoolBucket bucket = ArrayPools.GetOrAdd(Data.Length, _ => new ArrayPoolBucket());
            int poolCount = Interlocked.Increment(ref bucket.Count);
            if (poolCount <= MaxPooledArrays)
            {
                bucket.Arrays.Add(Data);
                return;
            }

            Interlocked.Decrement(ref bucket.Count);
        }

        public int Length => Data.Length;

        public T this[int x, int y, int z]
        {
            get => Data[ToIndex(x, y, z)];
            set => Data[ToIndex(x, y, z)] = value;
        }

        public void Clear()
        {
            Array.Clear(Data, 0, Data.Length);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ToIndex(int x, int y, int z)
        {
            return x + y * Width + z * SliceStride;
        }
    }
}
