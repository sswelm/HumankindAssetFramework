using System;
using System.Collections;
using System.Reflection;
using HumankindAssetFramework;
using UnityEngine;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    public class MeshScaleLifetimeTests
    {
        public struct Vertex { public Vector3 Pos; }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Persisted_or_copied_scaled_positions_are_not_scaled_twice(bool copy)
        {
            WithMesh(-1, (key, pos) => {
                var vertices = new[] { new Vertex(), new Vertex { Pos = new Vector3(10, 0, 0) } };
                Assert.Equal(.8f, UniversalInject.ScaleMeshVertices(key, vertices, 0, 2, pos, .8f));
                if (copy) vertices = (Vertex[])vertices.Clone();
                Assert.Equal(1f, UniversalInject.ScaleMeshVertices(key, vertices, 0, 2, pos, .8f));
                Assert.Equal(8f, vertices[1].Pos.x);
                UniversalInject.ScaleMeshVertices(key, vertices, 0, 2, pos, .4f);
                Assert.Equal(4f, vertices[1].Pos.x);
            });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Rebuilt_positions_with_a_zero_first_vertex_are_scaled_from_vanilla(bool replace)
        {
            WithMesh(-2, (key, pos) => {
                var vertices = new[] { new Vertex(), new Vertex { Pos = new Vector3(10, 0, 0) } };
                UniversalInject.ScaleMeshVertices(key, vertices, 0, 2, pos, .8f);
                if (replace) vertices = new Vertex[2];
                vertices[1].Pos = new Vector3(10, 0, 0);
                Assert.Equal(.8f, UniversalInject.ScaleMeshVertices(key, vertices, 0, 2, pos, .8f));
                Assert.Equal(8f, vertices[1].Pos.x);
            });
        }

        [Fact]
        public void A_changed_mesh_range_does_not_reuse_another_mesh_record()
        {
            WithMesh(-3, (key, pos) => {
                var vertices = new[] { new Vertex { Pos = new Vector3(10, 0, 0) }, new Vertex { Pos = new Vector3(8, 0, 0) } };
                UniversalInject.ScaleMeshVertices(key, vertices, 0, 1, pos, .8f);
                Assert.Equal(.8f, UniversalInject.ScaleMeshVertices(key, vertices, 1, 1, pos, .8f));
                Assert.Equal(6.4f, vertices[1].Pos.x);
            });
        }

        static void WithMesh(long key, Action<long, FieldInfo> test)
        {
            var records = (IDictionary)typeof(UniversalInject).GetField("meshApplied", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
            try { test(key, typeof(Vertex).GetField("Pos")); }
            finally { records.Remove(key); }
        }
    }
}
