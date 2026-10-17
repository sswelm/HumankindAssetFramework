// BlenderNumpy.cs - the numpy arithmetic vehicle_rig.py's rotor axle runs through, ported for bit parity with Blender 5.1.2's own
// numpy 2.3.4 (2026-10-17, step (d) part d4 of the vehicle_rig port; docs/Review-Backlog.md item (5)). Blender builds numpy without
// an external BLAS ("blas: auto, lapack: lapack", no OpenBLAS), so:
//   - P.mean(0), np.cov's average and its dot(X, X.T) are SEQUENTIAL double sums with no fused multiply-add (measured: 400 of 400
//     random clouds equal a plain Python loop bit for bit), the covariance scaled by `* (1.0 / (n - 1))`;
//   - np.linalg.eigh is numpy's bundled lapack_lite - the f2c translation of reference LAPACK 3.2 - DSYEVD('V', 'L'): for a 3x3
//     DLANSY's scaling guard, DSYTRD -> DSYTD2 (Householder: DLARFG with the old scale/ssq DNRM2 and DLAPY2, DSYMV, DDOT, DAXPY,
//     DSYR2), DSTEDC('I') -> DSTEQR (implicit QL/QR, DLAEV2, DLARTG, DLASR, the selection sort of the eigenpairs), DORMTR ->
//     DORMQR -> DORM2R -> DLARF (DGEMV, DGER) applying the reflectors to the eigenvectors. Ported operation for operation, 1-based
//     as the f2c code is; the eigenvector's SIGN falls out of these steps, so nothing here may be "simplified".
// The rescaling branches (a matrix norm outside sqrt(safmin/eps) .. sqrt(eps/safmin), DSTEQR's ssfmin/ssfmax) are not ported: a
// call that would need them returns null and the caller leaves the job to Blender by name.
using System;
using System.Collections.Generic;

public static class BlenderNumpy
{
    const double Eps = 1.1102230246251565e-16;          // dlamch("Epsilon"): 2^-53 (LAPACK 3.2's dlamc2 on IEEE doubles, rounding)
    const double SafeMin = 2.2250738585072014e-308;     // dlamch("Safe minimum"): 2^-1022
    const double Precision = 2.220446049250313e-16;     // dlamch("Precision"): eps * base

    /// <summary>numpy: `P = np.array(pts); P = P - P.mean(0); np.cov(P.T)` for points given as doubles (float32 values), the 3x3
    /// covariance row-major. Null for fewer than two points (n - 1 = 0).</summary>
    public static double[] CovOfCentred(IList<double[]> pts)
    {
        int n = pts.Count;
        if (n < 2) return null;
        // P.mean(0): the column sums in row order, then / n
        double[] m0 = new double[3];
        foreach (var p in pts) for (int k = 0; k < 3; k++) m0[k] += p[k];
        for (int k = 0; k < 3; k++) m0[k] /= n;
        var X = new double[3][];
        for (int k = 0; k < 3; k++) { X[k] = new double[n]; for (int i = 0; i < n; i++) X[k][i] = pts[i][k] - m0[k]; }
        // np.cov: average(X, axis=1) - sequential again - and X -= avg
        for (int k = 0; k < 3; k++)
        {
            double s = 0.0; for (int i = 0; i < n; i++) s += X[k][i];
            double avg = s / n;
            for (int i = 0; i < n; i++) X[k][i] -= avg;
        }
        // c = dot(X, X.T); c *= true_divide(1, n - 1)
        double fact = 1.0 / (n - 1);
        var c = new double[9];
        for (int i = 0; i < 3; i++)
            for (int j = 0; j < 3; j++)
            {
                double s = 0.0; for (int t = 0; t < n; t++) s += X[i][t] * X[j][t];
                c[3 * i + j] = s * fact;
            }
        return c;
    }

    /// <summary>np.linalg.eigh(a) of a symmetric 3x3 (row-major), UPLO 'L': the eigenvalues ascending and the eigenvectors as
    /// columns (v[3 * row + col]). Null when a rescaling branch would run (DSYEVD's, DLARFG's, DLARTG's) or DSTEQR does not converge.</summary>
    public static (double[] w, double[] v)? Eigh3(double[] a)
    {
        const int n = 3;
        // the Fortran column-major array, 1-based: A(i, j) = A[i + j * 3] with offset -4
        var A = new double[1 + n * n + n];
        double Ag(int i, int j) => A[i + (j - 1) * n];
        for (int i = 1; i <= n; i++) for (int j = 1; j <= n; j++) A[i + (j - 1) * n] = a[3 * (i - 1) + (j - 1)];
        // DSYEVD: DLANSY('M', 'L') and the scaling guard
        double smlnum = SafeMin / Precision, bignum = 1.0 / smlnum, rmin = Math.Sqrt(smlnum), rmax = Math.Sqrt(bignum);
        double anrm = 0.0;
        for (int j = 1; j <= n; j++) for (int i = j; i <= n; i++) { double v = Math.Abs(Ag(i, j)); anrm = anrm >= v ? anrm : v; }
        if ((anrm > 0.0 && anrm < rmin) || anrm > rmax) return null;
        var d = new double[n + 1]; var e = new double[n]; var tau = new double[n];
        var Z = new double[1 + n * n + n];
        try
        {
            Dsytd2Lower(A, n, d, e, tau);
            // DSTEDC('I') -> DSTEQR('I'): Z starts as the identity
            for (int i = 1; i <= n; i++) Z[i + (i - 1) * n] = 1.0;
            if (!Dsteqr(n, d, e, Z)) return null;
        }
        catch (NotSupportedException) { return null; }   // DLARFG's or DLARTG's rescaling of a tiny or huge entry: not ported
        // DORMTR('L', 'L', 'N'): DORMQR on rows 2..n of Z with the reflectors stored below A's subdiagonal -> DORM2R
        Dorm2rLeftN(n - 1, n, n - 1, A, 2, 1, tau, Z, 2, 1);
        var w = new double[3]; var vec = new double[9];
        for (int i = 0; i < 3; i++) w[i] = d[i + 1];
        for (int i = 1; i <= n; i++) for (int j = 1; j <= n; j++) vec[3 * (i - 1) + (j - 1)] = Z[i + (j - 1) * n];
        return (w, vec);
    }

    // ---------------------------------------------------------------- DSYTD2 (UPLO 'L')

    static void Dsytd2Lower(double[] A, int n, double[] d, double[] e, double[] tau)
    {
        // A(i, j) at A[i + (j - 1) * n]; the subvectors addressed by their first element's flat index
        for (int i = 1; i <= n - 1; i++)
        {
            int m = n - i;
            int alphaAt = (i + 1) + (i - 1) * n, xAt = Math.Min(i + 2, n) + (i - 1) * n;
            double taui = Dlarfg(m, A, alphaAt, A, xAt);
            e[i] = A[alphaAt];
            if (taui != 0.0)
            {
                A[alphaAt] = 1.0;
                // DSYMV('L', m, taui, A(i+1, i+1), lda, v = A(i+1, i), 1, 0, tau(i), 1): y = tau[i..i+m-1]
                var y = new double[m + 1];
                int a0 = (i + 1) + i * n;   // A(i+1, i+1)
                Dsymv(m, taui, A, a0, n, A, alphaAt, y);
                // alpha = taui * -.5 * DDOT(m, tau(i), 1, v, 1)
                double dot = Ddot(m, y, 1, A, alphaAt);
                double alpha = taui * -.5 * dot;
                // DAXPY(m, alpha, v, 1, tau(i), 1)
                Daxpy(m, alpha, A, alphaAt, y, 1);
                // DSYR2('L', m, -1, v, 1, tau(i), 1, A(i+1, i+1), lda)
                Dsyr2(m, -1.0, A, alphaAt, y, 1, A, a0, n);
                A[alphaAt] = e[i];   // (DSYTD2 uses tau(i..) as the scratch y; tau(i) = taui below is all that survives)
            }
            d[i] = A[i + (i - 1) * n];
            tau[i] = taui;
        }
        d[n] = A[n + (n - 1) * n];
    }

    /// <summary>DLARFG(n, alpha, x, 1, tau): alpha is arr[alphaAt], x the n-1 entries from xAt. Returns tau.</summary>
    static double Dlarfg(int n, double[] aArr, int alphaAt, double[] xArr, int xAt)
    {
        if (n <= 1) return 0.0;
        double xnorm = Dnrm2(n - 1, xArr, xAt);
        if (xnorm == 0.0) return 0.0;
        double alpha = aArr[alphaAt];
        double beta = -DSign(Dlapy2(alpha, xnorm), alpha);
        double safmin = SafeMin / Eps;
        if (Math.Abs(beta) < safmin) throw new NotSupportedException("DLARFG's rescaling");
        double tau = (beta - alpha) / beta;
        double sc = 1.0 / (alpha - beta);
        Dscal(n - 1, sc, xArr, xAt);
        aArr[alphaAt] = beta;
        return tau;
    }

    static double DSign(double a, double b) { double x = Math.Abs(a); return b >= 0 ? x : -x; }

    static double Dnrm2(int n, double[] x, int at)
    {
        if (n < 1) return 0.0;
        if (n == 1) return Math.Abs(x[at]);
        double scale = 0.0, ssq = 1.0;
        for (int k = 0; k < n; k++)
        {
            double xv = x[at + k];
            if (xv != 0.0)
            {
                double absxi = Math.Abs(xv);
                if (scale < absxi) { double r = scale / absxi; ssq = ssq * (r * r) + 1.0; scale = absxi; }
                else { double r = absxi / scale; ssq += r * r; }
            }
        }
        return scale * Math.Sqrt(ssq);
    }

    static double Dlapy2(double x, double y)
    {
        double xabs = Math.Abs(x), yabs = Math.Abs(y);
        double w = xabs >= yabs ? xabs : yabs, z = xabs <= yabs ? xabs : yabs;
        if (z == 0.0) return w;
        double r = z / w;
        return w * Math.Sqrt(r * r + 1.0);
    }

    static void Dscal(int n, double da, double[] x, int at)
    {
        // incx 1: m = n % 5 leading singles, then blocks of five - every element da * x, the order cannot change a product
        for (int k = 0; k < n; k++) x[at + k] = da * x[at + k];
    }

    /// <summary>DDOT(n, x(1..), 1, y(1..), 1) with x in xs from xAt, y in ys from yAt: for n &lt; 5 a sequential sum.</summary>
    static double Ddot(int n, double[] xs, int xAt, double[] ys, int yAt)
    {
        double dtemp = 0.0;
        if (n <= 0) return 0.0;
        int m = n % 5;
        for (int i = 0; i < m; i++) dtemp += xs[xAt + i] * ys[yAt + i];
        if (n < 5) return dtemp;
        for (int i = m; i < n; i += 5)
            dtemp = dtemp + xs[xAt + i] * ys[yAt + i] + xs[xAt + i + 1] * ys[yAt + i + 1] + xs[xAt + i + 2] * ys[yAt + i + 2] + xs[xAt + i + 3] * ys[yAt + i + 3] + xs[xAt + i + 4] * ys[yAt + i + 4];
        return dtemp;
    }

    /// <summary>DAXPY(n, da, x, 1, y, 1): y += da * x (the unrolled blocks of four do the same per element).</summary>
    static void Daxpy(int n, double da, double[] xs, int xAt, double[] ys, int yAt)
    {
        if (n <= 0 || da == 0.0) return;
        for (int i = 0; i < n; i++) ys[yAt + i] += da * xs[xAt + i];
    }

    /// <summary>DSYMV('L', n, alpha, A(a0..), lda, x, 1, beta = 0, y, 1): y(1..n) in y[1..n].</summary>
    static void Dsymv(int n, double alpha, double[] A, int a0, int lda, double[] xs, int xAt, double[] y)
    {
        // beta = 0: y zeroed first
        for (int i = 1; i <= n; i++) y[i] = 0.0;
        if (alpha == 0.0) return;
        double Aij(int i, int j) => A[a0 + (i - 1) + (j - 1) * lda];
        for (int j = 1; j <= n; j++)
        {
            double temp1 = alpha * xs[xAt + j - 1], temp2 = 0.0;
            y[j] += temp1 * Aij(j, j);
            for (int i = j + 1; i <= n; i++) { y[i] += temp1 * Aij(i, j); temp2 += Aij(i, j) * xs[xAt + i - 1]; }
            y[j] += alpha * temp2;
        }
    }

    /// <summary>DSYR2('L', n, alpha, x, 1, y(1..n) in y[1..], 1, A(a0..), lda).</summary>
    static void Dsyr2(int n, double alpha, double[] xs, int xAt, double[] y, int yAt1, double[] A, int a0, int lda)
    {
        if (n == 0 || alpha == 0.0) return;
        for (int j = 1; j <= n; j++)
        {
            double xj = xs[xAt + j - 1], yj = y[yAt1 + j - 1];
            if (xj != 0.0 || yj != 0.0)
            {
                double temp1 = alpha * yj, temp2 = alpha * xj;
                for (int i = j; i <= n; i++)
                {
                    int at = a0 + (i - 1) + (j - 1) * lda;
                    A[at] = A[at] + xs[xAt + i - 1] * temp1 + y[yAt1 + i - 1] * temp2;
                }
            }
        }
    }

    // ---------------------------------------------------------------- DSTEQR ('I')

    static bool Dsteqr(int n, double[] d, double[] e, double[] Z)
    {
        double eps = Eps, eps2 = eps * eps, safmin = SafeMin, safmax = 1.0 / safmin;
        double ssfmax = Math.Sqrt(safmax) / 3.0, ssfmin = Math.Sqrt(safmin) / eps2;
        var work = new double[2 * n - 1];   // work(1 .. 2n-2), 1-based: work[l] and work[n - 1 + l]
        int nmaxit = n * 30, jtot = 0, l1 = 1, nm1 = n - 1;
        int l, lsv, lend, lendsv, m;
        double p, g, r, c, s, f, b, rt1, rt2;
        while (true)
        {
            // L10
            if (l1 > n) break;
            if (l1 > 1) e[l1 - 1] = 0.0;
            m = n;
            if (l1 <= nm1)
            {
                for (int mm = l1; mm <= nm1; mm++)
                {
                    double tst = Math.Abs(e[mm]);
                    if (tst == 0.0) { m = mm; goto L30; }
                    if (tst <= Math.Sqrt(Math.Abs(d[mm])) * Math.Sqrt(Math.Abs(d[mm + 1])) * eps) { e[mm] = 0.0; m = mm; goto L30; }
                }
                m = n;
            }
        L30:
            l = l1; lsv = l; lend = m; lendsv = lend; l1 = m + 1;
            if (lend == l) continue;
            // DLANST('I') of the block
            double anorm;
            {
                int nn = lend - l + 1;
                if (nn == 1) anorm = Math.Abs(d[l]);
                else
                {
                    double v1 = Math.Abs(d[l]) + Math.Abs(e[l]), v2 = Math.Abs(e[l + nn - 2]) + Math.Abs(d[l + nn - 1]);
                    anorm = v1 >= v2 ? v1 : v2;
                    for (int ii = 2; ii <= nn - 1; ii++) { double v = Math.Abs(d[l + ii - 1]) + Math.Abs(e[l + ii - 1]) + Math.Abs(e[l + ii - 2]); anorm = anorm >= v ? anorm : v; }
                }
            }
            if (anorm == 0.0) continue;
            if (anorm > ssfmax || anorm < ssfmin) return false;   // DLASCL rescaling: not ported
            if (Math.Abs(d[lend]) < Math.Abs(d[l])) { lend = lsv; l = lendsv; }
            if (lend > l)
            {
                // QL iteration
                while (true)
                {
                    // L40
                    m = lend;
                    if (l != lend)
                        for (int mm = l; mm <= lend - 1; mm++)
                        {
                            double t = Math.Abs(e[mm]); double tst = t * t;
                            if (tst <= eps2 * Math.Abs(d[mm]) * Math.Abs(d[mm + 1]) + safmin) { m = mm; break; }
                        }
                    if (m < lend) e[m] = 0.0;
                    p = d[l];
                    if (m == l) { d[l] = p; l++; if (l <= lend) continue; break; }
                    if (m == l + 1)
                    {
                        Dlaev2(d[l], e[l], d[l + 1], out rt1, out rt2, out c, out s);
                        work[l] = c; work[n - 1 + l] = s;
                        DlasrRVB(n, 2, work, l, n - 1 + l, Z, l, n);
                        d[l] = rt1; d[l + 1] = rt2; e[l] = 0.0;
                        l += 2;
                        if (l <= lend) continue;
                        break;
                    }
                    if (jtot == nmaxit) break;
                    jtot++;
                    g = (d[l + 1] - p) / (e[l] * 2.0);
                    r = Dlapy2(g, 1.0);
                    g = d[m] - p + e[l] / (g + DSign(r, g));
                    s = 1.0; c = 1.0; p = 0.0;
                    for (int i = m - 1; i >= l; i--)
                    {
                        f = s * e[i]; b = c * e[i];
                        Dlartg(g, f, out c, out s, out r);
                        if (i != m - 1) e[i + 1] = r;
                        g = d[i + 1] - p;
                        r = (d[i] - g) * s + c * 2.0 * b;
                        p = s * r;
                        d[i + 1] = g + p;
                        g = c * r - b;
                        work[i] = c; work[n - 1 + i] = -s;
                    }
                    DlasrRVB(n, m - l + 1, work, l, n - 1 + l, Z, l, n);
                    d[l] -= p; e[l] = g;
                }
            }
            else
            {
                // QR iteration
                while (true)
                {
                    // L90
                    m = lend;
                    if (l != lend)
                        for (int mm = l; mm >= lend + 1; mm--)
                        {
                            double t = Math.Abs(e[mm - 1]); double tst = t * t;
                            if (tst <= eps2 * Math.Abs(d[mm]) * Math.Abs(d[mm - 1]) + safmin) { m = mm; break; }
                        }
                    if (m > lend) e[m - 1] = 0.0;
                    p = d[l];
                    if (m == l) { d[l] = p; l--; if (l >= lend) continue; break; }
                    if (m == l - 1)
                    {
                        Dlaev2(d[l - 1], e[l - 1], d[l], out rt1, out rt2, out c, out s);
                        work[m] = c; work[n - 1 + m] = s;
                        DlasrRVF(n, 2, work, m, n - 1 + m, Z, l - 1, n);
                        d[l - 1] = rt1; d[l] = rt2; e[l - 1] = 0.0;
                        l -= 2;
                        if (l >= lend) continue;
                        break;
                    }
                    if (jtot == nmaxit) break;
                    jtot++;
                    g = (d[l - 1] - p) / (e[l - 1] * 2.0);
                    r = Dlapy2(g, 1.0);
                    g = d[m] - p + e[l - 1] / (g + DSign(r, g));
                    s = 1.0; c = 1.0; p = 0.0;
                    for (int i = m; i <= l - 1; i++)
                    {
                        f = s * e[i]; b = c * e[i];
                        Dlartg(g, f, out c, out s, out r);
                        if (i != m) e[i - 1] = r;
                        g = d[i] - p;
                        r = (d[i + 1] - g) * s + c * 2.0 * b;
                        p = s * r;
                        d[i] = g + p;
                        g = c * r - b;
                        work[i] = c; work[n - 1 + i] = s;
                    }
                    DlasrRVF(n, l - m + 1, work, m, n - 1 + m, Z, m, n);
                    d[l] -= p; e[l - 1] = g;
                }
            }
            // L140 (no rescale undo: rescaling declined above)
            if (jtot < nmaxit) continue;
            for (int i = 1; i <= n - 1; i++) if (e[i] != 0.0) return false;   // no convergence
            return true;
        }
        // L160: the selection sort of the eigenvalues, the vectors swapped with them
        for (int ii = 2; ii <= n; ii++)
        {
            int i = ii - 1, k = i; p = d[i];
            for (int j = ii; j <= n; j++) if (d[j] < p) { k = j; p = d[j]; }
            if (k != i)
            {
                d[k] = d[i]; d[i] = p;
                for (int row = 1; row <= n; row++) { int ai = row + (i - 1) * n, ak = row + (k - 1) * n; double t = Z[ai]; Z[ai] = Z[ak]; Z[ak] = t; }
            }
        }
        return true;
    }

    static void Dlaev2(double a, double b, double c, out double rt1, out double rt2, out double cs1, out double sn1)
    {
        double sm = a + c, df = a - c, adf = Math.Abs(df), tb = b + b, ab = Math.Abs(tb), acmx, acmn, rt, cs, ct, tn;
        int sgn1, sgn2;
        if (Math.Abs(a) > Math.Abs(c)) { acmx = a; acmn = c; } else { acmx = c; acmn = a; }
        if (adf > ab) { double q = ab / adf; rt = adf * Math.Sqrt(q * q + 1.0); }
        else if (adf < ab) { double q = adf / ab; rt = ab * Math.Sqrt(q * q + 1.0); }
        else rt = ab * Math.Sqrt(2.0);
        if (sm < 0.0) { rt1 = (sm - rt) * .5; sgn1 = -1; rt2 = acmx / rt1 * acmn - b / rt1 * b; }
        else if (sm > 0.0) { rt1 = (sm + rt) * .5; sgn1 = 1; rt2 = acmx / rt1 * acmn - b / rt1 * b; }
        else { rt1 = rt * .5; rt2 = rt * -.5; sgn1 = 1; }
        if (df >= 0.0) { cs = df + rt; sgn2 = 1; } else { cs = df - rt; sgn2 = -1; }
        double acs = Math.Abs(cs);
        if (acs > ab) { ct = -tb / cs; sn1 = 1.0 / Math.Sqrt(ct * ct + 1.0); cs1 = ct * sn1; }
        else
        {
            if (ab == 0.0) { cs1 = 1.0; sn1 = 0.0; }
            else { tn = -cs / tb; cs1 = 1.0 / Math.Sqrt(tn * tn + 1.0); sn1 = tn * cs1; }
        }
        if (sgn1 == sgn2) { tn = cs1; cs1 = -sn1; sn1 = tn; }
    }

    static void Dlartg(double f, double g, out double cs, out double sn, out double r)
    {
        double safmin = SafeMin, eps = Eps;
        // safmn2 = base ** int(log(safmin / eps) / log(base) / 2): 2^-484 on IEEE doubles
        int expo = (int)(Math.Log(safmin / eps) / Math.Log(2.0) / 2.0);
        double safmn2 = Math.Pow(2.0, expo), safmx2 = 1.0 / safmn2;
        if (g == 0.0) { cs = 1.0; sn = 0.0; r = f; return; }
        if (f == 0.0) { cs = 0.0; sn = 1.0; r = g; return; }
        double f1 = f, g1 = g, scale = Math.Abs(f1) >= Math.Abs(g1) ? Math.Abs(f1) : Math.Abs(g1);
        if (scale >= safmx2 || scale <= safmn2) throw new NotSupportedException("DLARTG's rescaling");
        r = Math.Sqrt(f1 * f1 + g1 * g1);
        cs = f1 / r; sn = g1 / r;
        if (Math.Abs(f) > Math.Abs(g) && cs < 0.0) { cs = -cs; sn = -sn; r = -r; }
    }

    /// <summary>DLASR('R', 'V', 'B', m, nn, c = work(cAt..), s = work(sAt..), A = Z(1, col0..), lda).</summary>
    static void DlasrRVB(int m, int nn, double[] work, int cAt, int sAt, double[] Z, int col0, int lda)
    {
        for (int j = nn - 1; j >= 1; j--)
        {
            double ctemp = work[cAt + j - 1], stemp = work[sAt + j - 1];
            if (ctemp != 1.0 || stemp != 0.0)
                for (int i = 1; i <= m; i++)
                {
                    int aj1 = i + (col0 + j - 1) * lda, aj = i + (col0 + j - 2) * lda;
                    double temp = Z[aj1];
                    Z[aj1] = ctemp * temp - stemp * Z[aj];
                    Z[aj] = stemp * temp + ctemp * Z[aj];
                }
        }
    }

    /// <summary>DLASR('R', 'V', 'F', ...).</summary>
    static void DlasrRVF(int m, int nn, double[] work, int cAt, int sAt, double[] Z, int col0, int lda)
    {
        for (int j = 1; j <= nn - 1; j++)
        {
            double ctemp = work[cAt + j - 1], stemp = work[sAt + j - 1];
            if (ctemp != 1.0 || stemp != 0.0)
                for (int i = 1; i <= m; i++)
                {
                    int aj1 = i + (col0 + j - 1) * lda, aj = i + (col0 + j - 2) * lda;
                    double temp = Z[aj1];
                    Z[aj1] = ctemp * temp - stemp * Z[aj];
                    Z[aj] = stemp * temp + ctemp * Z[aj];
                }
        }
    }

    // ---------------------------------------------------------------- DORM2R ('L', 'N') through DLARF

    /// <summary>DORM2R('L', 'N', m, nc, k, A(aRow0, aCol0..), lda = 3, tau, C(cRow0, cCol0..), ldc = 3): Q * C with
    /// Q = H(1) .. H(k), applied from H(k) down to H(1).</summary>
    static void Dorm2rLeftN(int m, int nc, int k, double[] A, int aRow0, int aCol0, double[] tau, double[] C, int cRow0, int cCol0)
    {
        const int lda = 3;
        int AIdx(int i, int j) => (aRow0 + i - 1) + (aCol0 + j - 2) * lda;
        int CIdx(int i, int j) => (cRow0 + i - 1) + (cCol0 + j - 2) * lda;
        for (int i = k; i >= 1; i--)
        {
            int mi = m - i + 1, ic = i;
            double aii = A[AIdx(i, i)];
            A[AIdx(i, i)] = 1.0;
            // DLARF('L', mi, nc, v = A(i, i).., 1, tau(i), C(ic, 1), ldc)
            int vAt = AIdx(i, i);
            double t = tau[i];
            int lastv = 0, lastc = 0;
            if (t != 0.0)
            {
                lastv = mi;
                int ii = (lastv - 1) + 1;
                while (lastv > 0 && A[vAt + ii - 1] == 0.0) { lastv--; ii--; }
                // ILADLC(lastv, nc, C(ic, 1), ldc): the last non-zero column
                int Cv(int r, int col) => CIdx(ic + r - 1, col);
                if (C[Cv(1, nc)] != 0.0 || C[Cv(lastv, nc)] != 0.0) lastc = nc;
                else
                {
                    lastc = 0;
                    for (int col = nc; col >= 1; col--)
                    {
                        bool any = false;
                        for (int r = 1; r <= lastv; r++) if (C[Cv(r, col)] != 0.0) { any = true; break; }
                        if (any) { lastc = col; break; }
                    }
                }
                if (lastv > 0)
                {
                    // DGEMV('T', lastv, lastc, 1, C, ldc, v, 1, 0, work, 1)
                    var work = new double[lastc + 1];
                    for (int col = 1; col <= lastc; col++)
                    {
                        double temp = 0.0;
                        for (int r = 1; r <= lastv; r++) temp += C[Cv(r, col)] * A[vAt + r - 1];
                        work[col] += 1.0 * temp;
                    }
                    // DGER(lastv, lastc, -tau, v, 1, work, 1, C, ldc)
                    double alpha = -t;
                    for (int col = 1; col <= lastc; col++)
                        if (work[col] != 0.0)
                        {
                            double temp = alpha * work[col];
                            for (int r = 1; r <= lastv; r++) C[Cv(r, col)] += A[vAt + r - 1] * temp;
                        }
                }
            }
            A[AIdx(i, i)] = aii;
        }
    }
}
