// BlenderEigen.cs - Eigen's 4x4 float inverse as Blender's `invert_m4_m4` reaches it (EIG_invert_m4_m4:
// Map<Matrix4f>.computeInverseWithCheck; Eigen/src/LU/arch/InverseSize4.h at the revision Blender 5.1.2 bundles,
// 8a1083e9: Intel's SSE algorithm over four 2x2 blocks). Every lane operation is an IEEE float32 multiply, add,
// subtract or - for the determinant's reciprocal - a true division: Blender's build has no FMA, so Eigen's
// `preciprocal` is `pdiv(pset1(1), det)` and the rcp-plus-Newton variant is compiled out. Measured on 2026-10-07
// against the binary's own invert_m4_m4 (reached through bpy.ops.object.parent_set, which stores invert_m4_m4(parent)
// in matrix_parent_inverse): five matrices - three affine, one dense with a non-trivial last row, one bone's
// matrix_local - equal bit for bit, signed zeros included; the generic cofactor path, and the rcp variants with or
// without FMA, are not. NOTE: mathutils' Matrix.inverted() and inverted_safe() are NOT this call (the adjugate,
// VehicleProbe.InvertedSafe) - a first measurement through inverted() misled a day's port.
// The input and output are Blender's float[4][4] (M[col][row]), which is Eigen's column-major Matrix4f: the same
// sixteen floats. A zero determinant (Eigen's own determinant) gives the zero matrix, as EIG_invert_m4_m4 does.
// Used where Blender's C calls invert_m4_m4 on a path that reaches a written file: armature_finalize_restpose, which
// turns edit bones into bones (step 5 d, part 4b).
using System;

public static class BlenderEigen
{
    struct P { public float a, b, c, d; public P(float a, float b, float c, float d) { this.a = a; this.b = b; this.c = c; this.d = d; } public float this[int i] => i == 0 ? a : i == 1 ? b : i == 2 ? c : d; }

    static P Mul(P x, P y) => new P((float)(x.a * y.a), (float)(x.b * y.b), (float)(x.c * y.c), (float)(x.d * y.d));
    static P Add(P x, P y) => new P((float)(x.a + y.a), (float)(x.b + y.b), (float)(x.c + y.c), (float)(x.d + y.d));
    static P Sub(P x, P y) => new P((float)(x.a - y.a), (float)(x.b - y.b), (float)(x.c - y.c), (float)(x.d - y.d));
    /// <summary>_mm_shuffle_ps(x, y, mask(p, q, r, s)): (x[p], x[q], y[r], y[s]).</summary>
    static P Sw(P x, P y, int p, int q, int r, int s) => new P(x[p], x[q], y[r], y[s]);
    static P MoveLH(P x, P y) => new P(x.a, x.b, y.a, y.b);   // _mm_movelh_ps
    static P MoveHL(P x, P y) => new P(y.c, y.d, x.c, x.d);   // _mm_movehl_ps
    static P Dup(P x, int p) => new P(x[p], x[p], x[p], x[p]);

    /// <summary>The inverse of a Blender float[4][4] (sixteen floats, M[col][row]); the zero matrix when Eigen's determinant
    /// is zero.</summary>
    public static float[] InvertM4(float[] m)
    {
        if (Determinant(m) == 0f) return new float[16];
        var L1 = new P(m[0], m[1], m[2], m[3]); var L2 = new P(m[4], m[5], m[6], m[7]); var L3 = new P(m[8], m[9], m[10], m[11]); var L4 = new P(m[12], m[13], m[14], m[15]);
        // the four 2x2 blocks (storage orders match: movelh / movehl)
        P A = MoveLH(L1, L2), B = MoveHL(L2, L1), C = MoveLH(L3, L4), D = MoveHL(L4, L3);
        // AB = A# B, DC = D# C
        P AB = Mul(Sw(A, A, 3, 3, 0, 0), B); AB = Sub(AB, Mul(Sw(A, A, 1, 1, 2, 2), Sw(B, B, 2, 3, 0, 1)));
        P DC = Mul(Sw(D, D, 3, 3, 0, 0), C); DC = Sub(DC, Mul(Sw(D, D, 1, 1, 2, 2), Sw(C, C, 2, 3, 0, 1)));
        // the blocks' determinants
        P dA = Mul(Sw(A, A, 3, 3, 1, 1), A); dA = Sub(dA, MoveHL(dA, dA));
        P dB = Mul(Sw(B, B, 3, 3, 1, 1), B); dB = Sub(dB, MoveHL(dB, dB));
        P dC = Mul(Sw(C, C, 3, 3, 1, 1), C); dC = Sub(dC, MoveHL(dC, dC));
        P dD = Mul(Sw(D, D, 3, 3, 1, 1), D); dD = Sub(dD, MoveHL(dD, dD));
        P d = Mul(Sw(DC, DC, 0, 2, 1, 3), AB); d = Add(d, MoveHL(d, d)); d = Add(d, Sw(d, d, 1, 0, 0, 0));
        P d1 = Mul(dA, dD), d2 = Mul(dB, dC);
        // det = |A||D| + |B||C| - trace(A# B D# C); rd = 1 / det, a true division (no FMA: no Newton step)
        P det = Dup(Sub(Add(d1, d2), d), 0);
        P rd = new P((float)(1.0f / det.a), (float)(1.0f / det.b), (float)(1.0f / det.c), (float)(1.0f / det.d));
        // the inverse's blocks
        P iD = Mul(Sw(C, C, 0, 0, 2, 2), MoveLH(AB, AB)); iD = Add(iD, Mul(Sw(C, C, 1, 1, 3, 3), MoveHL(AB, AB))); iD = Sub(Mul(D, Dup(dA, 0)), iD);
        P iA = Mul(Sw(B, B, 0, 0, 2, 2), MoveLH(DC, DC)); iA = Add(iA, Mul(Sw(B, B, 1, 1, 3, 3), MoveHL(DC, DC))); iA = Sub(Mul(A, Dup(dD, 0)), iA);
        P iB = Mul(D, Sw(AB, AB, 3, 0, 3, 0)); iB = Sub(iB, Mul(Sw(D, D, 1, 0, 3, 2), Sw(AB, AB, 2, 1, 2, 1))); iB = Sub(Mul(C, Dup(dB, 0)), iB);
        P iC = Mul(A, Sw(DC, DC, 3, 0, 3, 0)); iC = Sub(iC, Mul(Sw(A, A, 1, 0, 3, 2), Sw(DC, DC, 2, 1, 2, 1))); iC = Sub(Mul(B, Dup(dC, 0)), iC);
        // rd with lanes 1 and 2 negated (pxor with the PNNP sign mask), then each block times it
        rd = new P(rd.a, -rd.b, -rd.c, rd.d);
        iA = Mul(iA, rd); iB = Mul(iB, rd); iC = Mul(iC, rd); iD = Mul(iD, rd);
        var r = new float[16];
        Store(r, 0, Sw(iA, iB, 3, 1, 3, 1)); Store(r, 4, Sw(iA, iB, 2, 0, 2, 0)); Store(r, 8, Sw(iC, iD, 3, 1, 3, 1)); Store(r, 12, Sw(iC, iD, 2, 0, 2, 0));
        return r;
    }

    static void Store(float[] r, int at, P p) { r[at] = p.a; r[at + 1] = p.b; r[at + 2] = p.c; r[at + 3] = p.d; }

    /// <summary>Eigen's determinant (determinant_impl for 4: 2x2 minors, then 3x3s), computed here in double: only its
    /// zero-ness decides anything (computeInverseWithCheck with a threshold of 0).</summary>
    static float Determinant(float[] m)
    {
        double[] a = new double[16]; for (int i = 0; i < 16; i++) a[i] = m[i];
        double M(int r, int c) => a[c * 4 + r];
        double Det3(int r0, int r1, int r2, int c0, int c1, int c2) =>
            M(r0, c0) * (M(r1, c1) * M(r2, c2) - M(r1, c2) * M(r2, c1)) - M(r0, c1) * (M(r1, c0) * M(r2, c2) - M(r1, c2) * M(r2, c0)) + M(r0, c2) * (M(r1, c0) * M(r2, c1) - M(r1, c1) * M(r2, c0));
        double det = M(0, 0) * Det3(1, 2, 3, 1, 2, 3) - M(0, 1) * Det3(1, 2, 3, 0, 2, 3) + M(0, 2) * Det3(1, 2, 3, 0, 1, 3) - M(0, 3) * Det3(1, 2, 3, 0, 1, 2);
        return (float)det;
    }
}
