using Unity.Collections.LowLevel.Unsafe;
using UnityEngine;
using UnityEngine.Rendering;
using static MagicPhysX.NativeMethods;

namespace PhysX
{
    public static class QueryExtensions
    {
        public static Vector2 GetUV(this SweepHit sweepHit, int channel)
        {
            unsafe
            {
                if (sweepHit.collider is not MeshCollider meshCollider || meshCollider.convex)
                {
                    return default;
                }

                if (!TryGetTriangleData(meshCollider, sweepHit.faceIndex, channel, out var pos0, out var pos1, out var pos2, out var uv0, out var uv1, out var uv2))
                {
                    return default;
                }

                var localHit = sweepHit.collider.transform.InverseTransformPoint(sweepHit.position);
                BarycentricCoords(localHit, pos0, pos1, pos2, out var baryU, out var baryV);
                var baryW = 1f - baryU - baryV;
                return baryW * uv0 + baryU * uv1 + baryV * uv2;
            }
        }

        public static Vector2 GetUV(this RaycastHit raycastHit, int channel)
        {
            unsafe
            {
                if (raycastHit.collider is not MeshCollider meshCollider || meshCollider.convex)
                {
                    return default;
                }

                if (!TryGetTriangleData(meshCollider, raycastHit.faceIndex, channel, out _, out _, out _, out var uv0, out var uv1, out var uv2))
                {
                    return default;
                }

                var baryU = raycastHit.u;
                var baryV = raycastHit.v;
                var baryW = 1.0f - baryU - baryV;
                return baryW * uv0 + baryU * uv1 + baryV * uv2;
            }
        }

        private static unsafe bool TryGetTriangleData(
            MeshCollider meshCollider,
            int faceIndex,
            int channel,
            out Vector3 pos0,
            out Vector3 pos1,
            out Vector3 pos2,
            out Vector2 uv0,
            out Vector2 uv1,
            out Vector2 uv2)
        {
            pos0 = default;
            pos1 = default;
            pos2 = default;
            uv0 = default;
            uv1 = default;
            uv2 = default;

            var mesh = meshCollider.sharedMesh;
            if (mesh == null || !mesh.isReadable || meshCollider.triangleMesh == null || faceIndex < 0)
            {
                return false;
            }

            var vertexAttribute = GetUvVertexAttribute(channel);
            if (!mesh.HasVertexAttribute(vertexAttribute))
            {
                return false;
            }

            var trianglesRemap = PxTriangleMesh_getTrianglesRemap(meshCollider.triangleMesh);
            var remappedFaceIndex = trianglesRemap != null ? (int)trianglesRemap[faceIndex] : faceIndex;
            var baseIndex = remappedFaceIndex * 3;

            var meshDataArray = Mesh.AcquireReadOnlyMeshData(mesh);
            try
            {
                var meshData = meshDataArray[0];

                var vertIndex0 = ReadIndex(meshData, baseIndex + 0);
                var vertIndex1 = ReadIndex(meshData, baseIndex + 1);
                var vertIndex2 = ReadIndex(meshData, baseIndex + 2);

                pos0 = ReadVector3(mesh, meshData, VertexAttribute.Position, vertIndex0);
                pos1 = ReadVector3(mesh, meshData, VertexAttribute.Position, vertIndex1);
                pos2 = ReadVector3(mesh, meshData, VertexAttribute.Position, vertIndex2);

                uv0 = ReadVector2(mesh, meshData, vertexAttribute, vertIndex0);
                uv1 = ReadVector2(mesh, meshData, vertexAttribute, vertIndex1);
                uv2 = ReadVector2(mesh, meshData, vertexAttribute, vertIndex2);
                return true;
            }
            finally
            {
                meshDataArray.Dispose();
            }
        }

        private static VertexAttribute GetUvVertexAttribute(int channel)
        {
            switch (channel)
            {
                case 1: return VertexAttribute.TexCoord1;
                case 2: return VertexAttribute.TexCoord2;
                case 3: return VertexAttribute.TexCoord3;
                case 4: return VertexAttribute.TexCoord4;
                case 5: return VertexAttribute.TexCoord5;
                case 6: return VertexAttribute.TexCoord6;
                case 7: return VertexAttribute.TexCoord7;
                default: return VertexAttribute.TexCoord0;
            }
        }

        private static int ReadIndex(Mesh.MeshData meshData, int index)
        {
            return meshData.indexFormat == IndexFormat.UInt16
                ? meshData.GetIndexData<ushort>()[index]
                : meshData.GetIndexData<int>()[index];
        }

        private static unsafe Vector3 ReadVector3(Mesh mesh, Mesh.MeshData meshData, VertexAttribute attribute, int index)
        {
            var stream = mesh.GetVertexAttributeStream(attribute);
            var offset = meshData.GetVertexAttributeOffset(attribute);
            var stride = meshData.GetVertexBufferStride(stream);
            var vertexData = meshData.GetVertexData<byte>(stream);
            var pointer = (byte*)vertexData.GetUnsafeReadOnlyPtr();
            return *(Vector3*)(pointer + offset + stride * index);
        }

        private static unsafe Vector2 ReadVector2(Mesh mesh, Mesh.MeshData meshData, VertexAttribute attribute, int index)
        {
            var stream = mesh.GetVertexAttributeStream(attribute);
            var offset = meshData.GetVertexAttributeOffset(attribute);
            var stride = meshData.GetVertexBufferStride(stream);
            var vertexData = meshData.GetVertexData<byte>(stream);
            var pointer = (byte*)vertexData.GetUnsafeReadOnlyPtr();
            return *(Vector2*)(pointer + offset + stride * index);
        }

        private static void BarycentricCoords(Vector3 point, Vector3 vertA, Vector3 vertB, Vector3 vertC, out float baryU, out float baryV)
        {
            var edgeAB = vertB - vertA;
            var edgeAC = vertC - vertA;
            var toPoint = point - vertA;
            var dotABAB = Vector3.Dot(edgeAB, edgeAB);
            var dotABAC = Vector3.Dot(edgeAB, edgeAC);
            var dotACAC = Vector3.Dot(edgeAC, edgeAC);
            var dotPointAB = Vector3.Dot(toPoint, edgeAB);
            var dotPointAC = Vector3.Dot(toPoint, edgeAC);
            var denom = dotABAB * dotACAC - dotABAC * dotABAC;
            baryU = (dotACAC * dotPointAB - dotABAC * dotPointAC) / denom;
            baryV = (dotABAB * dotPointAC - dotABAC * dotPointAB) / denom;
        }
    }
}
