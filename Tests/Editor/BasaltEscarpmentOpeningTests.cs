using System.Collections.Generic;
using LoogaSoft.Terrain.Geology;
using NUnit.Framework;
using UnityEngine;

namespace LoogaSoft.Terrain.Tests
{
    public sealed class BasaltEscarpmentOpeningTests
    {
        [Test]
        public void RecessOpening_LowersTopAndKeepsColumnBase()
        {
            GameObject owner = new("Opening");
            try
            {
                owner.transform.position = new Vector3(0f, 50f, 0f);
                BasaltEscarpmentOpening opening =
                    owner.AddComponent<BasaltEscarpmentOpening>();
                Matrix4x4 matrix = Matrix4x4.TRS(new Vector3(0f, 50f, 0f),
                    Quaternion.identity, new Vector3(20f, 100f, 20f));
                var placements = new List<BasaltColumnPlacement>
                {
                    new(matrix, 6, 0, 0.5f, 100f, 20f, 0)
                };

                BasaltEscarpmentOpening.ApplyAll(placements,
                    new[] { opening });

                Assert.That(placements, Has.Count.EqualTo(1));
                BasaltColumnPlacement changed = placements[0];
                Vector3 bottom = changed.Matrix.MultiplyPoint3x4(Vector3.down * 0.5f);
                Vector3 top = changed.Matrix.MultiplyPoint3x4(Vector3.up * 0.5f);
                Assert.That(bottom.y, Is.EqualTo(0f).Within(0.0001f));
                Assert.That(top.y, Is.LessThanOrEqualTo(50f));
                Assert.That(changed.Height, Is.LessThan(100f));
            }
            finally
            {
                Object.DestroyImmediate(owner);
            }
        }
    }
}
