// PoseStreamParity.cs - the acceptance row's rule for two routes' baked pose streams (editor/RigRouteAcceptanceTest.cs): the
// SDK's ClipCollection.Reimport samples FrameCount = ceil(clip length x frame rate) frames of BonesCount bones and encodes each
// bone in a format chosen from its motion (three/four 32-bit words per frame, three per pair for rotation-only,
// a held bone once). Pure C#: the Tests project
// compiles this file (the QuadEstimate pattern).
using System;

public static class PoseStreamParity
{
    /// <summary>Null when the two baked streams agree: the same bone count; the same frame count, or ONE frame apart - the
    /// SDK's ceil of (length x frame rate) lands on either side of a whole number, Unity's FBX import reading a 212-frame
    /// clip as 8.833334 s where the GLB route's clip is 8.833333 s (measured 2026-10-27: 213 against 212 frames, the
    /// ArmouredCar 8 against 7); and the byte lengths must be compatible with the SDK's word encodings. An extra frame adds
    /// zero, 12 or 16 bytes per bone (rotation-only frames are packed in pairs; held bones are stored once).</summary>
    public static string Compare(int framesA, int bonesA, long bytesA, int framesB, int bonesB, long bytesB)
    {
        if (bonesA != bonesB) return $"the baked pose stream holds {bonesA} bones on the FBX route, {bonesB} on the GLB route";
        if (bonesA <= 0) return "the baked pose stream has no bones";
        if (framesA <= 0 || framesB <= 0) return $"the baked pose stream holds {framesA} frames on the FBX route, {framesB} on the GLB route";
        if (bytesA < 12L * bonesA || bytesB < 12L * bonesB || bytesA % 4 != 0 || bytesB % 4 != 0)
            return "the baked pose stream has an invalid encoded word length";
        if (Math.Abs((long)framesA - framesB) > 1) return $"the baked pose stream is {framesA} frames on the FBX route, {framesB} on the GLB route";
        if (framesA == framesB)
            return bytesA == bytesB ? null : $"the baked pose stream is {bytesA} bytes on the FBX route, {bytesB} on the GLB route ({framesA} frames, {bonesA} bones on both: the bone encodings differ)";
        long diff = bytesA - bytesB;
        if (diff == 0) return null; // held bones and an already allocated rotation-frame pair add no bytes, in either direction
        bool direction = (framesA > framesB) == (diff > 0);
        long words = Math.Abs(diff) / 4;
        // A sum of n three/four-word increments occupies 3n..4n words. No possible bone encoding adds one, two or five words.
        if (!direction || (words + 3) / 4 > Math.Min(words / 3, bonesA))
            return $"the baked pose stream is {bytesA} bytes for {framesA} frames on the FBX route, {bytesB} bytes for {framesB} frames on the GLB route (one frame apart, but the byte difference is not a frame of {bonesA} bones)";
        return null;
    }
}
