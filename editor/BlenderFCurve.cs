// BlenderFCurve.cs - the value of an fcurve of Bezier keys at a time, as Blender 5.1 computes it (replacing
// deploy_convert.py; fcurve.cc: fcurve_eval_keyframes, BKE_fcurve_correct_bezpart, findzero, solve_cubic, berekeny).
// The time is solved for the curve parameter in DOUBLE - Cardano's formula through the C runtime's exp, log, acos and
// cos (BlenderTrig) - and the value is the cubic in that parameter in FLOAT. tools/deploy_drill.sh holds it to
// Blender's own FCurve.evaluate() on generated curves, bit for bit, every branch of the solver.
using System;
using System.Collections.Generic;

public static class BlenderFCurve
{
    const float FltEpsilon = 1.1920929e-07f;
    static readonly float Small = (float)(-1.0e-10);

    /// <summary>For the drill: set to an array of Branches.Length and every evaluation counts the branches it takes.</summary>
    public static int[] Hits;
    public static readonly string[] Branches =
    {
        "at or before the first key",
        "at or past the last key",
        "on a middle key (within 0.0001 frame)",
        "within 1e-8 of the next key",
        "outside the segment found (0)",
        "two keys on one frame",
        "all at one height",
        "no root in range (0)",
        "the first key's handle cut back",
        "the second key's handle cut back",
        "cubic, one real root",
        "cubic, zero discriminant: the first root",
        "cubic, zero discriminant: the second root",
        "cubic, three real roots: the first",
        "cubic, three real roots: the second",
        "cubic, three real roots: the third",
        "quadratic: the first root",
        "quadratic: the second root",
        "quadratic, zero discriminant",
        "linear",
        "no equation left (0)",
    };
    static void Hit(int i) { var h = Hits; if (h != null) h[i]++; }

    /// <summary>fcurve_eval_keyframes for keys that are all BEZIER (no modifiers, no cycles): the first or the last key
    /// held outside the range (CONSTANT extrapolation) or its handle's slope carried on (LINEAR).</summary>
    public static float Evaluate(IList<BlenderDeploy.ArmKey> keys, float evaltime, bool constant = true)
    {
        int n = keys.Count;
        if (evaltime <= keys[0].Frame) { Hit(0); return Extrapolate(keys[0], keys[0].LeftX, keys[0].LeftY, evaltime, constant); }
        if (keys[n - 1].Frame <= evaltime) { Hit(1); return Extrapolate(keys[n - 1], keys[n - 1].RightX, keys[n - 1].RightY, evaltime, constant); }

        int a = Search(keys, evaltime, out bool exact);
        var bezt = keys[a];
        if (exact) { Hit(2); return bezt.Value; }
        var prev = a > 0 ? keys[a - 1] : bezt;
        if (Math.Abs((float)(bezt.Frame - evaltime)) < 1.0e-8f) { Hit(3); return bezt.Value; }
        if (evaltime < prev.Frame || bezt.Frame < evaltime) { Hit(4); return 0.0f; }
        float duration = (float)(bezt.Frame - prev.Frame);
        if (duration == 0f) { Hit(5); return prev.Value; }

        float v1x = prev.Frame, v1y = prev.Value, v2x = prev.RightX, v2y = prev.RightY;
        float v3x = bezt.LeftX, v3y = bezt.LeftY, v4x = bezt.Frame, v4y = bezt.Value;
        // all four at one height: that height
        if (Math.Abs((float)(v1y - v4y)) < FltEpsilon && Math.Abs((float)(v2y - v3y)) < FltEpsilon && Math.Abs((float)(v3y - v4y)) < FltEpsilon) { Hit(6); return v1y; }
        CorrectBezpart(v1x, v1y, ref v2x, ref v2y, ref v3x, ref v3y, v4x, v4y);
        if (!FindZero(evaltime, v1x, v2x, v3x, v4x, out float t)) { Hit(7); return 0.0f; }
        // berekeny
        float c0 = v1y;
        float c1 = (float)(3.0f * (float)(v2y - v1y));
        float c2 = (float)(3.0f * (float)((float)(v1y - (float)(2.0f * v2y)) + v3y));
        float c3 = (float)((float)(v4y - v1y) + (float)(3.0f * (float)(v2y - v3y)));
        float tt = (float)(t * t);
        return (float)((float)((float)(c0 + (float)(t * c1)) + (float)(tt * c2)) + (float)((float)(tt * t) * c3));
    }

    static float Extrapolate(BlenderDeploy.ArmKey end, float handleX, float handleY, float evaltime, bool constant)
    {
        if (constant) return end.Value;
        float dx = (float)(end.Frame - evaltime);
        float fac = (float)(end.Frame - handleX);
        if (fac == 0.0f) return end.Value;
        fac = (float)((float)(end.Value - handleY) / fac);
        return (float)(end.Value - (float)(fac * dx));
    }

    static bool IsEqt(float a, float b, float c) => a > b ? (float)(a - b) <= c : (float)(b - a) <= c;

    // BKE_fcurve_bezt_binarysearch_index_ex with the evaluation's threshold of 0.0001 frame
    static int Search(IList<BlenderDeploy.ArmKey> keys, float frame, out bool exact)
    {
        const float threshold = 0.0001f;
        int start = 0, end = keys.Count; exact = false;
        float framenum = keys[0].Frame;
        if (IsEqt(frame, framenum, threshold)) { exact = true; return 0; }
        if (frame < framenum) return 0;
        framenum = keys[keys.Count - 1].Frame;
        if (IsEqt(frame, framenum, threshold)) { exact = true; return keys.Count - 1; }
        if (frame > framenum) return keys.Count;
        while (start <= end)
        {
            int mid = start + ((end - start) / 2);
            float midfra = keys[mid].Frame;
            if (IsEqt(frame, midfra, threshold)) { exact = true; return mid; }
            if (frame > midfra) start = mid + 1; else end = mid - 1;
        }
        return start;
    }

    // a handle longer than the keys are apart is cut back to that span: the curve must not loop in time
    static void CorrectBezpart(float v1x, float v1y, ref float v2x, ref float v2y, ref float v3x, ref float v3y, float v4x, float v4y)
    {
        float h1x = (float)(v1x - v2x), h1y = (float)(v1y - v2y), h2x = (float)(v4x - v3x), h2y = (float)(v4y - v3y);
        float len = (float)(v4x - v1x), len1 = Math.Abs(h1x), len2 = Math.Abs(h2x);
        if ((float)(len1 + len2) == 0.0f) return;
        if (len1 > len)
        {
            Hit(8); 
            float fac = (float)(len / len1);
            v2x = (float)(v1x - (float)(fac * h1x)); v2y = (float)(v1y - (float)(fac * h1y));
        }
        if (len2 > len)
        {
            Hit(9); 
            float fac = (float)(len / len2);
            v3x = (float)(v4x - (float)(fac * h2x)); v3y = (float)(v4y - (float)(fac * h2y));
        }
    }

    static bool InRange(float o) => o >= Small && o <= 1.000001f;

    static double Sqrt3d(double d)
    {
        if (d == 0.0) return 0.0;
        if (d < 0.0) return -BlenderTrig.Exp(BlenderTrig.Log(-d) / 3.0);
        return BlenderTrig.Exp(BlenderTrig.Log(d) / 3.0);
    }

    // findzero + solve_cubic: the FIRST root in -1e-10 .. 1.000001, in the order the formula gives them
    static bool FindZero(float x, float q0, float q1, float q2, float q3, out float root)
    {
        double c0 = (float)(q0 - x);
        double c1 = (float)(3.0f * (float)(q1 - q0));
        double c2 = (float)(3.0f * (float)((float)(q0 - (float)(2.0f * q1)) + q2));
        double c3 = (float)((float)(q3 - q0) + (float)(3.0f * (float)(q1 - q2)));
        double a, b, c, p, q, d, t, phi; float o;
        root = 0f;
        if (c3 != 0.0)
        {
            a = c2 / c3; b = c1 / c3; c = c0 / c3; a = a / 3;
            p = b / 3 - a * a;
            q = (2 * a * a * a - a * b + c) / 2;
            d = q * q + p * p * p;
            if (d > 0.0)
            {
                t = Math.Sqrt(d);
                o = (float)(Sqrt3d(-q + t) + Sqrt3d(-q - t) - a);
                if (InRange(o)) { Hit(10); root = o; return true; }
                return false;
            }
            if (d == 0.0)
            {
                t = Sqrt3d(-q);
                o = (float)(2 * t - a);
                if (InRange(o)) { Hit(11); root = o; return true; }
                o = (float)(-t - a);
                if (InRange(o)) { Hit(12); root = o; return true; }
                return false;
            }
            phi = BlenderTrig.Acos(-q / Math.Sqrt(-(p * p * p)));
            t = Math.Sqrt(-p);
            p = BlenderTrig.Cos(phi / 3);
            q = Math.Sqrt(3 - 3 * p * p);
            o = (float)(2 * t * p - a);
            if (InRange(o)) { Hit(13); root = o; return true; }
            o = (float)(-t * (p + q) - a);
            if (InRange(o)) { Hit(14); root = o; return true; }
            o = (float)(-t * (p - q) - a);
            if (InRange(o)) { Hit(15); root = o; return true; }
            return false;
        }
        a = c2; b = c1; c = c0;
        if (a != 0.0)
        {
            p = b * b - 4 * a * c;
            if (p > 0)
            {
                p = Math.Sqrt(p);
                o = (float)((-b - p) / (2 * a));
                if (InRange(o)) { Hit(16); root = o; return true; }
                o = (float)((-b + p) / (2 * a));
                if (InRange(o)) { Hit(17); root = o; return true; }
                return false;
            }
            if (p == 0)
            {
                o = (float)(-b / (2 * a));
                if (InRange(o)) { Hit(18); root = o; return true; }
            }
            return false;
        }
        if (b != 0.0)
        {
            o = (float)(-c / b);
            if (InRange(o)) { Hit(19); root = o; return true; }
            return false;
        }
        if (c == 0.0) { Hit(20); root = 0.0f; return true; }
        return false;
    }
}
