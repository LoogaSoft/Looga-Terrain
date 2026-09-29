using System;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;

namespace LoogaSoft.Terrain
{
    /// <summary>Decodes and counts feedback on workers without retaining GPU readback memory.</summary>
    public sealed class VirtualTextureFeedbackBatch : IDisposable
    {
        /// <summary>Identifies a requested leaf page and its visible pixel count.</summary>
        public struct Request
        {
            public ulong Key;
            public int Count;
        }

        private NativeArray<float> _pixels;
        private NativeArray<ulong> _keys;
        private NativeList<Request> _results;
        private JobHandle _handle;
        private bool _pending;

        /// <summary>Reports whether scheduled analysis has completed.</summary>
        public bool IsCompleted => !_pending || _handle.IsCompleted;
        /// <summary>Reports whether analysis is scheduled.</summary>
        public bool Pending => _pending;

        /// <summary>Copies transient feedback and schedules decoding, sorting, and reduction.</summary>
        public void Schedule(NativeArray<float> pixels, int entryCount)
        {
            if (_pending)
            {
                throw new InvalidOperationException("Feedback analysis is already pending.");
            }
            if ((pixels.Length & 1) != 0)
            {
                throw new ArgumentException("Feedback requires page and tile pairs.");
            }
            int count = pixels.Length / 2;
            if (!_pixels.IsCreated || _pixels.Length != pixels.Length)
            {
                Dispose();
                _pixels = new NativeArray<float>(pixels.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _keys = new NativeArray<ulong>(count, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
                _results = new NativeList<Request>(count, Allocator.Persistent);
            }
            _pixels.CopyFrom(pixels);
            _results.Clear();
            _handle = new Decode { Pixels = _pixels, Keys = _keys, EntryCount = entryCount }.Schedule(count, 256);
            _handle = _keys.SortJob().Schedule(_handle);
            _handle = new Reduce { Keys = _keys, Results = _results }.Schedule(_handle);
            _pending = true;
            JobHandle.ScheduleBatchedJobs();
        }

        /// <summary>Waits for worker completion during explicit offline validation or synchronization.</summary>
        public void WaitForCompletion()
        {
            _handle.Complete();
        }

        /// <summary>Completes analysis and returns storage valid until the next schedule or disposal.</summary>
        public NativeArray<Request> Complete()
        {
            _handle.Complete();
            _pending = false;
            return _results.AsArray();
        }

        [BurstCompile]
        private struct Decode : IJobParallelFor
        {
            [ReadOnly] public NativeArray<float> Pixels;
            [WriteOnly] public NativeArray<ulong> Keys;
            public int EntryCount;

            public void Execute(int index)
            {
                float page = Pixels[index * 2];
                float tile = Pixels[index * 2 + 1];
                // Comparison also rejects NaN and infinity before conversion.
                bool valid = page >= 1 && page <= EntryCount && tile >= 1 && tile <= 16777216;
                Keys[index] = valid && page == (uint)page && tile == (uint)tile
                    ? ((ulong)(uint)tile << 32) | ((uint)page - 1) : 0;
            }
        }

        [BurstCompile]
        private struct Reduce : IJob
        {
            [ReadOnly] public NativeArray<ulong> Keys;
            public NativeList<Request> Results;

            public void Execute()
            {
                int index = 0;
                while (index < Keys.Length)
                {
                    ulong key = Keys[index];
                    int start = index++;
                    while (index < Keys.Length && Keys[index] == key)
                    {
                        index++;
                    }
                    if (key != 0)
                    {
                        Results.AddNoResize(new Request { Key = key, Count = index - start });
                    }
                }
            }
        }

        /// <summary>Completes pending work before releasing native storage.</summary>
        public void Dispose()
        {
            _handle.Complete();
            _pending = false;
            if (_pixels.IsCreated)
            {
                _pixels.Dispose();
            }
            if (_keys.IsCreated)
            {
                _keys.Dispose();
            }
            if (_results.IsCreated)
            {
                _results.Dispose();
            }
        }
    }
}
