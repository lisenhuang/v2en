using System.Runtime.InteropServices;

namespace v2en.Utilities;

/// <summary>
/// Pack/unpack a <c>float[]</c> to/from a little-endian byte[] BLOB for SQLite storage, plus
/// the unit-normalize + dot-product helpers used by retrieval (cosine == dot for unit vectors).
/// </summary>
public static class VectorBytes
{
    public static byte[] Pack(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        MemoryMarshal.AsBytes(vector.AsSpan()).CopyTo(bytes);
        return bytes;
    }

    public static float[] Unpack(byte[] blob)
    {
        var floats = new float[blob.Length / sizeof(float)];
        blob.AsSpan(0, floats.Length * sizeof(float)).CopyTo(MemoryMarshal.AsBytes(floats.AsSpan()));
        return floats;
    }

    /// <summary>Returns a unit-length copy of the vector (no-op if zero).</summary>
    public static float[] Normalize(float[] v)
    {
        double sum = 0;
        for (int i = 0; i < v.Length; i++) sum += (double)v[i] * v[i];
        var norm = Math.Sqrt(sum);
        if (norm <= 1e-12) return v;
        var inv = (float)(1.0 / norm);
        var result = new float[v.Length];
        for (int i = 0; i < v.Length; i++) result[i] = v[i] * inv;
        return result;
    }

    /// <summary>Dot product == cosine similarity when both vectors are unit-normalized.</summary>
    public static double Dot(float[] a, float[] b)
    {
        int n = Math.Min(a.Length, b.Length);
        double sum = 0;
        for (int i = 0; i < n; i++) sum += (double)a[i] * b[i];
        return sum;
    }
}
