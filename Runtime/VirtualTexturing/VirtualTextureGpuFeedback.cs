using System;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace LoogaSoft.Terrain
{
    /// <summary>Counts unique GPU requests before readback. Overflow uses the original pixel feedback path.</summary>
    internal sealed class VirtualTextureGpuFeedback : IDisposable
    {
        private readonly ComputeShader _compute;
        private readonly GraphicsBuffer _hash;
        private readonly GraphicsBuffer _compact;
        private readonly GraphicsBuffer _header;
        private readonly int _capacity;
        private bool _pending;
        private bool _disposed;
        private bool _released;

        internal VirtualTextureGpuFeedback(ComputeShader shader, int pixels)
        {
            _capacity = Mathf.Clamp(Mathf.NextPowerOfTwo(pixels * 2), 256, 131072);
            try
            {
                _compute = UnityEngine.Object.Instantiate(shader);
                _compute.hideFlags = HideFlags.HideAndDontSave;
                _hash = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacity, 8);
                _compact = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _capacity, 8);
                _header = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2, 4);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void Record(CommandBuffer commands, RenderTexture feedback, int entryCount)
        {
            int clear = _compute.FindKernel("Clear"), count = _compute.FindKernel("Count"), compact = _compute.FindKernel("Compact");
            commands.SetComputeIntParam(_compute, "_Capacity", _capacity);
            commands.SetComputeIntParam(_compute, "_EntryCount", entryCount);
            commands.SetComputeIntParams(_compute, "_FeedbackSize", feedback.width, feedback.height);
            commands.SetComputeBufferParam(_compute, clear, "_Hash", _hash);
            commands.SetComputeBufferParam(_compute, clear, "_Header", _header);
            commands.DispatchCompute(_compute, clear, (_capacity + 63) / 64, 1, 1);
            commands.SetComputeTextureParam(_compute, count, "_Feedback", feedback);
            commands.SetComputeBufferParam(_compute, count, "_Hash", _hash);
            commands.SetComputeBufferParam(_compute, count, "_Header", _header);
            commands.DispatchCompute(_compute, count, (feedback.width + 7) / 8, (feedback.height + 7) / 8, 1);
            commands.SetComputeBufferParam(_compute, compact, "_Hash", _hash);
            commands.SetComputeBufferParam(_compute, compact, "_Compact", _compact);
            commands.SetComputeBufferParam(_compute, compact, "_Header", _header);
            commands.DispatchCompute(_compute, compact, (_capacity + 63) / 64, 1, 1);
        }

        internal void Read(Action<NativeArray<uint>, bool> completed)
        {
            if (_pending) throw new InvalidOperationException("GPU feedback readback is already pending.");
            _pending = true;
            AsyncGPUReadback.Request(_header, request =>
            {
                if (request.hasError || _disposed)
                {
                    Finish(completed, default, false);
                    return;
                }
                var header = request.GetData<uint>();
                int count = (int)header[0];
                if (header[1] != 0 || count > _capacity)
                {
                    Finish(completed, default, false);
                    return;
                }
                if (count == 0)
                {
                    Finish(completed, default, true);
                    return;
                }
                AsyncGPUReadback.Request(_compact, count * 8, 0, data =>
                    Finish(completed, data.hasError ? default : data.GetData<uint>(), !data.hasError));
            });
        }

        private void Finish(Action<NativeArray<uint>, bool> completed, NativeArray<uint> data, bool valid)
        {
            _pending = false;
            try
            {
                completed(data, valid && !_disposed);
            }
            finally
            {
                if (_disposed)
                {
                    Dispose();
                }
            }
        }

        public void Dispose()
        {
            _disposed = true;
            if (_pending || _released) return;
            _released = true;
            _hash?.Dispose();
            _compact?.Dispose();
            _header?.Dispose();
            if (Application.isPlaying)
            {
                UnityEngine.Object.Destroy(_compute);
            }
            else
            {
                UnityEngine.Object.DestroyImmediate(_compute);
            }
        }
    }
}
