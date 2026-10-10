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
        "handles: a key that is an extreme of its neighbours (flat)",
        "handles: the left handle stopped at the previous key's height",
        "handles: the right handle stopped at the next key's height",
        "handles: an end held flat (CONSTANT extrapolation)",
        "handles: two keys on one frame",
        "handles: a run of keys smoothed",
        "handles: a free first key (LINEAR extrapolation)",
        "handles: a free last key (LINEAR extrapolation)",
        "handles: a fixed end handle scaled to fit",
        "handles: the system has no finite solution (the handles stay)",
        "handles: an overshooting handle locked on the second look (at zero)",
        "handles: an overshooting handle locked at its limit",
        "handles: a locked handle released",
        "handles: two unknowns",
        "handles: a locked handle released a second time",
        "handles: a locked handle kept after two releases",
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

    // ---- the handles of AUTO_CLAMPED keys (fcurve.cc BKE_fcurve_handles_recalc_ex; curve.cc calchandleNurb_intern,
    //      BKE_nurb_handle_smooth_fcurve, bezier_handle_calc_smooth_fcurve, tridiagonal_solve_with_limits;
    //      math_solvers.cc BLI_tridiagonal_solve) - what keyframe_insert leaves on a curve whose smoothing is the
    //      default "Continuous Acceleration"

    const float FltMax = 3.40282347e+38f;

    /// <summary>BKE_fcurve_handles_recalc for a curve of Bezier keys in frame order, every handle AUTO_CLAMPED, no
    /// cycle, the smoothing CONT_ACCEL. Each handle lies a third of the way to the neighbour in time (through
    /// `6 / 2.5614 * 2.5614`). A key that is an extreme of its neighbours is flat, and so are the first and the last
    /// under CONSTANT extrapolation. The keys between them get the heights at which the curve's acceleration is
    /// continuous: a tridiagonal system per run of such keys, solved in double, re-solved with every handle that
    /// overshoots its neighbour's height locked there. A lone key keeps handles a frame to each side.</summary>
    public static void RecalcHandles(IList<BlenderDeploy.ArmKey> keys, bool constant = true)
    {
        int n = keys.Count;
        if (n < 2) { foreach (var k in keys) { k.LeftX = (float)(k.Frame - 1f); k.RightX = (float)(k.Frame + 1f); k.LeftY = k.RightY = k.Value; } return; }
        var locked = new bool[n];   // auto_handle_type: HD_AUTOTYPE_LOCKED_FINAL
        for (int i = 0; i < n; i++)
        {
            var k = keys[i]; float p2x = k.Frame, p2y = k.Value;
            bool hasPrev = i > 0, hasNext = i < n - 1;
            float p1x, p1y, p3x, p3y;
            if (!hasPrev) { p3x = keys[1].Frame; p3y = keys[1].Value; p1x = (float)((float)(2.0f * p2x) - p3x); p1y = (float)((float)(2.0f * p2y) - p3y); }
            else
            {
                p1x = keys[i - 1].Frame; p1y = keys[i - 1].Value;
                if (!hasNext) { p3x = (float)((float)(2.0f * p2x) - p1x); p3y = (float)((float)(2.0f * p2y) - p1y); } else { p3x = keys[i + 1].Frame; p3y = keys[i + 1].Value; }
            }
            float dax = (float)(p2x - p1x), day = (float)(p2y - p1y), dbx = (float)(p3x - p2x), dby = (float)(p3y - p2y);
            float lenA = dax, lenB = dbx;
            if (lenA == 0f) lenA = 1f;
            if (lenB == 0f) lenB = 1f;
            float tvx = (float)((float)(dbx / lenB) + (float)(dax / lenA)), tvy = (float)((float)(dby / lenB) + (float)(day / lenA));
            float len = (float)(6.0f / 2.5614f);
            len = (float)(len * 2.5614f);
            locked[i] = false;
            if (len != 0f)
            {
                bool leftViolate = false, rightViolate = false;
                lenA = (float)(lenA / len);
                k.LeftX = (float)(p2x + (float)(tvx * -lenA)); k.LeftY = (float)(p2y + (float)(tvy * -lenA));
                if (hasPrev && hasNext)
                {
                    float yd1 = (float)(p1y - p2y), yd2 = (float)(p3y - p2y);
                    if ((yd1 <= 0f && yd2 <= 0f) || (yd1 >= 0f && yd2 >= 0f)) { Hit(21); k.LeftY = p2y; locked[i] = true; }
                    else if (yd1 <= 0f) { if (p1y > k.LeftY) { Hit(22); k.LeftY = p1y; leftViolate = true; } }
                    else { if (p1y < k.LeftY) { Hit(22); k.LeftY = p1y; leftViolate = true; } }
                }
                lenB = (float)(lenB / len);
                k.RightX = (float)(p2x + (float)(tvx * lenB)); k.RightY = (float)(p2y + (float)(tvy * lenB));
                if (hasPrev && hasNext)
                {
                    float yd1 = (float)(p1y - p2y), yd2 = (float)(p3y - p2y);
                    if ((yd1 <= 0f && yd2 <= 0f) || (yd1 >= 0f && yd2 >= 0f)) { k.RightY = p2y; locked[i] = true; }
                    else if (yd1 <= 0f) { if (p3y < k.RightY) { Hit(23); k.RightY = p3y; rightViolate = true; } }
                    else { if (p3y > k.RightY) { Hit(23); k.RightY = p3y; rightViolate = true; } }
                }
                if (leftViolate || rightViolate)
                {
                    float h1x = (float)(k.LeftX - p2x), h2x = (float)(p2x - k.RightX);
                    if (leftViolate) k.RightY = (float)(p2y + (float)((float)((float)(p2y - k.LeftY) / h1x) * h2x));
                    else k.LeftY = (float)(p2y + (float)((float)((float)(p2y - k.RightY) / h2x) * h1x));
                }
            }
            // automatic ease in and out: the ends are flat when the curve is held outside its range
            if ((i == 0 || i == n - 1) && constant) { Hit(24); k.LeftY = k.RightY = p2y; locked[i] = true; }
            // two keys on one frame (or out of order): neither is smoothed
            if (hasPrev && keys[i - 1].Frame >= k.Frame) { Hit(25); locked[i - 1] = locked[i] = true; }
        }
        // BKE_nurb_handle_smooth_fcurve: every run of free keys, with the locked key (or the curve's end) on each side
        int start = 0, count = 1;
        for (int j = 1; j < n; j++)
        {
            if (locked[j]) { Smooth(keys, locked, start, count + 1); start = j; count = 1; }
            else count++;
        }
        if (count > 1) Smooth(keys, locked, start, count);
    }

    static void Smooth(IList<BlenderDeploy.ArmKey> keys, bool[] locked, int start, int count)
    {
        int total = keys.Count;
        if (count < 2) return;
        var first = keys[start]; var last = keys[start + count - 1];
        bool solveFirst = start == 0 && !locked[start], solveLast = start + count == total && !locked[start + count - 1];
        if (count == 2 && solveFirst == solveLast) return;
        Hit(26);
        float[] dx = new float[count], dy = new float[count], l = new float[count], a = new float[count], b = new float[count], c = new float[count], d = new float[count],
                h = new float[count], hmax = new float[count], hmin = new float[count];
        dx[0] = dy[0] = float.NaN;
        for (int i = 1, j = start + 1; i < count; i++, j++)
        {
            dx[i] = (float)(keys[j].Frame - keys[j - 1].Frame);
            dy[i] = (float)(keys[j].Value - keys[j - 1].Value);
        }
        l[0] = l[count - 1] = 1.0f;
        for (int i = 1; i < count - 1; i++) l[i] = (float)(dx[i + 1] / dx[i]);
        for (int i = 0; i < count; i++) { hmax[i] = FltMax; hmin[i] = -FltMax; }
        // auto clamp: a key's handle does not turn back, and does not pass its neighbour's height
        for (int i = 1; i < count; i++)
        {
            Clamp(hmax, hmin, i - 1, dy[i]);
            Clamp(hmax, hmin, i, (float)(dy[i] * l[i]));
        }
        float firstAdj = 0.0f, lastAdj = 0.0f;
        if (!solveFirst)
        {
            float tx = (float)(first.RightX - first.Frame), ty = (float)(first.RightY - first.Value);
            firstAdj = HandleAdj(ref tx, ref ty, dx[1]);
            a[0] = c[0] = 0.0f; b[0] = 1.0f; d[0] = ty;
        }
        else { a[0] = 0.0f; b[0] = 2.0f; c[0] = (float)(1.0f / l[1]); d[0] = dy[1]; }
        if (!solveLast)
        {
            float tx = (float)(last.Frame - last.LeftX), ty = (float)(last.Value - last.LeftY);
            lastAdj = HandleAdj(ref tx, ref ty, dx[count - 1]);
            a[count - 1] = c[count - 1] = 0.0f; b[count - 1] = 1.0f; d[count - 1] = ty;
        }
        else
        {
            int i = count - 1;
            a[i] = (float)(l[i] * l[i]); b[i] = (float)(2.0f * l[i]); c[i] = 0.0f; d[i] = (float)((float)(dy[i] * l[i]) * l[i]);
        }
        for (int i = 1; i < count - 1; i++)
        {
            a[i] = (float)(l[i] * l[i]);
            b[i] = (float)(2.0f * (float)(l[i] + 1));
            c[i] = (float)(1.0f / l[i + 1]);
            d[i] = (float)((float)((float)(dy[i] * l[i]) * l[i]) + dy[i + 1]);
        }
        if (count > 2 || solveLast) b[1] = (float)(b[1] + (float)(l[1] * firstAdj));
        if (count > 2 || solveFirst) b[count - 2] = (float)(b[count - 2] + lastAdj);
        if (!SolveWithLimits(a, b, c, d, h, hmin, hmax, count)) { Hit(30); return; }
        for (int i = 1, j = start + 1; i < count - 1; i++, j++)
        {
            var k = keys[j];
            k.LeftY = (float)(k.Value + (float)(-h[i] / l[i]));
            k.RightY = (float)(k.Value + h[i]);
        }
        // a free end: its other handle mirrors the solved one, in time as well
        if (solveFirst)
        {
            Hit(27);
            first.RightY = (float)(first.Value + h[0]);
            first.LeftX = (float)(first.Frame + (float)(first.Frame - first.RightX)); first.LeftY = (float)(first.Value + (float)(first.Value - first.RightY));
        }
        if (solveLast)
        {
            Hit(28);
            last.LeftY = (float)(last.Value + (float)(-h[count - 1] / l[count - 1]));
            last.RightX = (float)(last.Frame + (float)(last.Frame - last.LeftX)); last.RightY = (float)(last.Value + (float)(last.Value - last.LeftY));
        }
    }

    // bezier_clamp with no_reverse and no_overshoot both on
    static void Clamp(float[] hmax, float[] hmin, int i, float dy)
    {
        if (dy > 0) { hmax[i] = Math.Min(hmax[i], dy); hmin[i] = 0.0f; }
        else if (dy < 0) { hmax[i] = 0.0f; hmin[i] = Math.Max(hmin[i], dy); }
        else hmax[i] = hmin[i] = 0.0f;
    }

    // bezier_calc_handle_adj: a fixed handle whose length in time is not a third of the interval
    static float HandleAdj(ref float hx, ref float hy, float dx)
    {
        float fac = (float)(dx / (float)(hx + (float)(dx / 3.0f)));
        if (fac < 1.0f) { Hit(29); hx = (float)(hx * fac); hy = (float)(hy * fac); }
        return (float)(1.0f - (float)((float)(3.0f * hx) / dx));
    }

    static bool SolveWithLimits(float[] a, float[] b, float[] c, float[] d, float[] h, float[] hmin, float[] hmax, int count)
    {
        float[] a0 = (float[])a.Clone(), b0 = (float[])b.Clone(), c0 = (float[])c.Clone(), d0 = (float[])d.Clone();
        var isLocked = new bool[count]; var unlocks = new int[count];
        bool overshoot, unlocked;
        do
        {
            if (!SolveCyclic(a, b, c, d, h, count)) return false;
            bool all = false, lockedAny = false;
            overshoot = unlocked = false;
            do
            {
                for (int i = 0; i < count; i++)
                {
                    if (h[i] >= hmin[i] && h[i] <= hmax[i]) continue;
                    overshoot = true;
                    float target = h[i] > hmax[i] ? hmax[i] : hmin[i];
                    // first only the handles stopped at a height other than zero; all of them when there is none
                    if (target != 0.0f || all)
                    {
                        if (all) { Hit(31); }
                        Hit(32); isLocked[i] = true;
                        a[i] = c[i] = 0.0f; b[i] = 1.0f; d[i] = target;
                        lockedAny = true;
                    }
                }
                all = true;
            } while (overshoot && !lockedAny);
            if (!lockedAny)
                for (int i = 0; i < count; i++)
                {
                    if (isLocked[i] && unlocks[i] >= 2) Hit(36);
                    if (!isLocked[i] || unlocks[i] >= 2) continue;
                    // the handle wants to move where it may: let it go (twice at most)
                    float state = (float)((float)((float)((float)(a0[i] * h[(i + count - 1) % count]) + (float)(b0[i] * h[i])) + (float)(c0[i] * h[(i + 1) % count])) - d0[i]);
                    float relax = (float)(-state * b0[i]);
                    if ((relax > 0 && h[i] < hmax[i]) || (relax < 0 && h[i] > hmin[i]))
                    {
                        a[i] = a0[i]; b[i] = b0[i]; c[i] = c0[i]; d[i] = d0[i];
                        Hit(33); isLocked[i] = false; unlocks[i]++; unlocked = true;
                        if (unlocks[i] == 2) Hit(35);
                    }
                }
        } while (overshoot || unlocked);
        return true;
    }

    // BLI_tridiagonal_solve_cyclic for a system that is not cyclic (a[0] and c[count - 1] are zero here): two unknowns
    // go through its own two-row form, more through the plain solver
    static bool SolveCyclic(float[] a, float[] b, float[] c, float[] d, float[] x, int count)
    {
        if (count == 2)
        {
            Hit(34);
            var a2 = new[] { 0f, (float)(a[1] + c[1]) }; var c2 = new[] { (float)(a[0] + c[0]), 0f };
            return Solve(a2, b, c2, d, x, count);
        }
        if (a[0] != 0.0f || c[count - 1] != 0.0f) throw new InvalidOperationException("a cyclic handle system (not ported: no curve here cycles)");
        return Solve(a, b, c, d, x, count);
    }

    // BLI_tridiagonal_solve: the Thomas algorithm in double, the result rounded to float
    static bool Solve(float[] a, float[] b, float[] c, float[] d, float[] x, int count)
    {
        var c1 = new double[count]; var d1 = new double[count];
        double cPrev, dPrev, xPrev;
        c1[0] = cPrev = (double)c[0] / b[0];
        d1[0] = dPrev = (double)d[0] / b[0];
        int i;
        for (i = 1; i < count; i++)
        {
            double denum = b[i] - a[i] * cPrev;
            c1[i] = cPrev = c[i] / denum;
            d1[i] = dPrev = (d[i] - a[i] * dPrev) / denum;
        }
        xPrev = dPrev;
        x[--i] = (float)xPrev;
        while (--i >= 0)
        {
            xPrev = d1[i] - c1[i] * xPrev;
            x[i] = (float)xPrev;
        }
        return !double.IsNaN(xPrev) && !double.IsInfinity(xPrev);
    }
}
