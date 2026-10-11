// PyFloatRepr.cs - Python's repr() of a float, as json.dumps writes a number (deploy_convert.py's export, part 8c):
// the shortest decimal that reads back as the same double (David Gay's dtoa mode 0: of the shortest candidates, the
// nearest to the value), written as Python does - plain digits when the decimal exponent lies in -4..15, else
// d.ddde±XX with at least two exponent digits; a trailing ".0" on a plain integral value (which __fix_json turns into
// an int before json.dumps sees it, so the glTF never shows it). Shortest is found by trying 1..17 significant digits:
// at each count the two decimals around the value are tested against the double's rounding interval in exact
// integer arithmetic (the interval is asymmetric at a power of two, so the nearer one alone would not do - the
// reviewer's find, 2026-10-11); the first count with one inside is the shortest, both inside gives the nearer, a tie
// the even last digit.
using System;
using System.Globalization;
using System.Numerics;

public static class PyFloatRepr
{
    public static string Repr(double v)
    {
        if (double.IsNaN(v) || double.IsInfinity(v)) throw new ArgumentException("json.dumps(allow_nan=False) refuses " + v);
        if (v == 0) return (1 / v) < 0 ? "-0.0" : "0.0";
        bool neg = v < 0; double a = Math.Abs(v);
        // exact decimal expansion of the binary value: a = mant * 2^exp
        long bits = BitConverter.DoubleToInt64Bits(a);
        int e2 = (int)((bits >> 52) & 0x7FF); long m = bits & 0xFFFFFFFFFFFFFL;
        if (e2 == 0) e2 = 1; else m |= 1L << 52;
        e2 -= 1075;
        BigInteger num = m, den = BigInteger.One;
        if (e2 >= 0) num <<= e2; else den <<= -e2;
        // decimal exponent: a = 0.d1d2... * 10^k, with d1 != 0
        int k = (int)Math.Floor(Math.Log10(a)) + 1;
        // adjust k exactly: 10^(k-1) <= a < 10^k
        while (Compare(num, den, k) >= 0) k++;
        while (Compare(num, den, k - 1) < 0) k--;
        // the rounding interval of a: a decimal D reads back as a iff it lies strictly between the midpoints to the
        // neighbouring doubles, or on a midpoint when a's mantissa is even (round half to even). prev is half as far
        // below when a sits on a power of two. Exact, in integers: 2*D compared with a+prev and a+next over one
        // denominator (double.Parse is not correctly rounded on .NET Framework, so it cannot judge this)
        bool mEven = (m & 1) == 0;
        // in units of u = 2^(e2-1): a = 2m u, next = (2m+2) u, prev = (2m-2) u - or (2m-1) u when a sits on a power of
        // two (the gap below is half as wide); u = uNum/uDen
        BigInteger uNum = BigInteger.One, uDen = BigInteger.One;
        if (e2 - 1 >= 0) uNum <<= (e2 - 1); else uDen <<= (1 - e2);
        bool powerOfTwo = m == (1L << 52) && e2 > -1074;
        BigInteger lowSum = ((BigInteger)m * 4 - (powerOfTwo ? 1 : 2)) * uNum;   // (a + prev) * uDen
        BigInteger highSum = ((BigInteger)m * 4 + 2) * uNum;                     // (a + next) * uDen
        BigInteger sumDen = uDen;
        string digits = null; int used = 0;
        for (int n = 1; n <= 17; n++)
        {
            // the two n-digit decimals around a: floor and floor + 1 of a * 10^(n-k) (the rounding interval is not
            // symmetric at a power of two, where the nearest one can lie outside while the other lies inside)
            BigInteger sn = num, sd = den;
            if (n - k >= 0) sn *= BigInteger.Pow(10, n - k); else sd *= BigInteger.Pow(10, k - n);
            BigInteger lo = BigInteger.DivRem(sn, sd, out BigInteger rem), hi = lo + 1;
            BigInteger scaleNum = BigInteger.One, scaleDen = BigInteger.One;   // D = q * 10^(k-n)
            if (k - n >= 0) scaleNum = BigInteger.Pow(10, k - n); else scaleDen = BigInteger.Pow(10, n - k);
            bool Inside(BigInteger q)
            {
                BigInteger left = q * scaleNum * 2 * sumDen;               // 2D * scaleDen * sumDen
                int cl = left.CompareTo(lowSum * scaleDen), chh = left.CompareTo(highSum * scaleDen);
                return (cl > 0 || (cl == 0 && mEven)) && (chh < 0 || (chh == 0 && mEven));
            }
            bool loIn = lo > 0 && Inside(lo), hiIn = Inside(hi);
            BigInteger pick;
            if (loIn && hiIn)
            {
                // both read back: the nearer; a tie to the even last digit
                int c = (rem * 2).CompareTo(sd);
                pick = c < 0 ? lo : c > 0 ? hi : (lo.IsEven ? lo : hi);
            }
            else if (loIn) pick = lo;
            else if (hiIn) pick = hi;
            else continue;
            string s = pick.ToString(CultureInfo.InvariantCulture);
            int kk = k;
            if (s.Length > n) { s = s.Substring(0, n); kk++; }   // hi carried into a new digit (9.99 -> 10.0)
            digits = s; k = kk; used = n; break;
        }
        if (digits == null) throw new InvalidOperationException("no 17-digit decimal reads back as " + v.ToString("R", CultureInfo.InvariantCulture));
        digits = digits.TrimEnd('0'); if (digits.Length == 0) digits = "0";
        int exp10 = k - 1;   // d.ddd * 10^exp10
        string body;
        if (exp10 >= -4 && exp10 < 16)
        {
            if (exp10 >= 0)
            {
                if (digits.Length <= exp10 + 1) body = digits + new string('0', exp10 + 1 - digits.Length) + ".0";
                else body = digits.Substring(0, exp10 + 1) + "." + digits.Substring(exp10 + 1);
            }
            else body = "0." + new string('0', -exp10 - 1) + digits;
        }
        else
        {
            string mant = digits.Length > 1 ? digits.Substring(0, 1) + "." + digits.Substring(1) : digits;
            body = mant + "e" + (exp10 < 0 ? "-" : "+") + Math.Abs(exp10).ToString("00", CultureInfo.InvariantCulture);
        }
        return neg ? "-" + body : body;
    }

    /// <summary>json.dumps of a glTF number after __fix_json: an integral float is written as an int (a negative zero
    /// as 0), anything else as repr.</summary>
    public static string Json(double v)
    {
        if (!double.IsNaN(v) && !double.IsInfinity(v) && Math.Floor(v) == v)
        {
            // int(obj): the exact integer, however long (a float32 max of 3.4028234663852886e+38 is 39 digits)
            if (v == 0) return "0";
            long bits = BitConverter.DoubleToInt64Bits(Math.Abs(v));
            int e2 = (int)((bits >> 52) & 0x7FF); long m = bits & 0xFFFFFFFFFFFFFL;
            if (e2 == 0) e2 = 1; else m |= 1L << 52;
            e2 -= 1075;
            BigInteger whole = e2 >= 0 ? (BigInteger)m << e2 : (BigInteger)m >> -e2;
            return (v < 0 ? "-" : "") + whole.ToString(CultureInfo.InvariantCulture);
        }
        return Repr(v);
    }

    static string Mantissa(string s) => s.Length > 1 ? s.Substring(0, 1) + "." + s.Substring(1) : s;

    // sign of (num/den) - 10^p
    static int Compare(BigInteger num, BigInteger den, int p)
    {
        if (p >= 0) return num.CompareTo(den * BigInteger.Pow(10, p));
        return (num * BigInteger.Pow(10, -p)).CompareTo(den);
    }
}
