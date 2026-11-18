using System;
using System.Diagnostics;
using System.Text;
using Unity.Profiling;

namespace LoogaSoft.Terrain.Stamps
{
    /// <summary>Sections of a stamp update, for timing checks and the profiler.</summary>
    internal enum StampSection
    {
        ChangeCapture,
        HeightEvaluation,
        FieldBuild,
        HeightCompare,
        HeightCopy,
        HeightSync,
        TextureEvaluation,
        AlphamapWrite,
        PlacementTile,
        PlacementTextureLayers,
        PlacementReadbackWait,
        PlacementUnpack,
        DetailReadbackWait,
        InstanceSetWrite,
        DetailConversion,
        DetailWrite
    }

    /// <summary>
    /// Profiler markers and CPU times of the update sections. The times are kept only while <see cref="Enabled"/> is
    /// true, so a normal update only begins and ends the markers. Sections can be in other sections: field builds are
    /// part of the evaluation sections, and readback waits and unpacks are part of a placement tile. A GPU wait counts
    /// as CPU time of the section that waits.
    /// </summary>
    internal static class StampSections
    {
        private static readonly string[] Names =
        {
            "Change capture",
            "Height evaluation", "Field build", "Height compare (GPU wait)", "Height copy", "Height CPU sync",
            "Texture evaluation", "Alphamap write",
            "Placement tile", "Placement texture layers", "Placement readback wait", "Placement unpack",
            "Detail readback wait", "Instance set write", "Detail conversion", "Detail SetDetailLayer"
        };

        private static readonly ProfilerMarker[] Markers = CreateMarkers();
        private static readonly long[] Ticks = new long[Names.Length];
        private static readonly int[] Calls = new int[Names.Length];

        /// <summary>True keeps the section times until <see cref="Reset"/>.</summary>
        // The innermost open section (-1 = none) and the number of sections entered. A watchdog thread reads them while
        // the main thread is busy, so they are volatile.
        private static volatile int _current = -1;
        private static volatile int _entries;

        internal static bool Enabled { get; set; }

        /// <summary>The innermost open section, or "none". Safe to read from another thread.</summary>
        internal static string Current
        {
            get
            {
                int current = _current;
                return current < 0 ? "none" : Names[current];
            }
        }

        /// <summary>Sections entered since the editor started. A busy main thread that enters none is stuck.</summary>
        internal static int Entries => _entries;

        internal static Scope Measure(StampSection section)
        {
            return new Scope(section);
        }

        internal static void Reset()
        {
            Array.Clear(Ticks, 0, Ticks.Length);
            Array.Clear(Calls, 0, Calls.Length);
        }

        /// <summary>One line for each section with time: seconds and number of calls.</summary>
        internal static string Report(string indent)
        {
            var report = new StringBuilder();
            for (int i = 0; i < Names.Length; i++)
            {
                if (Calls[i] == 0) continue;
                report.Append(indent).Append(Names[i]).Append(": ")
                    .Append((Ticks[i] / (double)Stopwatch.Frequency).ToString("F3")).Append(" s (")
                    .Append(Calls[i]).Append(")\n");
            }
            return report.ToString();
        }

        private static ProfilerMarker[] CreateMarkers()
        {
            var markers = new ProfilerMarker[Names.Length];
            for (int i = 0; i < Names.Length; i++)
            {
                markers[i] = new ProfilerMarker("Looga Stamps." + Names[i]);
            }
            return markers;
        }

        internal readonly struct Scope : IDisposable
        {
            private readonly StampSection _section;
            private readonly long _start;
            private readonly int _outer;

            internal Scope(StampSection section)
            {
                _section = section;
                _start = Enabled ? Stopwatch.GetTimestamp() : 0;
                _outer = _current;
                _current = (int)section;
                _entries++;
                Markers[(int)section].Begin();
            }

            public void Dispose()
            {
                _current = _outer;
                Markers[(int)_section].End();
                if (_start == 0) return;
                Ticks[(int)_section] += Stopwatch.GetTimestamp() - _start;
                Calls[(int)_section]++;
            }
        }
    }
}
