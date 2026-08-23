using System;
using HumankindAssetFramework;
using Xunit;

namespace HumankindAssetFramework.Tests
{
    // THE DEAD-SENTINEL BUG CLASS (2026-08-23), pinned. The shape that motivated the typed member reads:
    //
    //     bool loaded = true; try { loaded = Convert.ToBoolean(GetMember(unit, "IsLoaded")); } catch { }
    //
    // reads as "true unless the game says otherwise" and means "FALSE whenever the member is missing" — because
    // GetMember returns null for a missing/renamed member and Convert.ToBoolean(null) is false rather than a throw,
    // so the catch never runs and the initializer is dead. Two live sites then did `if (!loaded) continue;`, which
    // on a game rename would have skipped their work silently and forever.
    //
    // Same family as the dead-default TryParse (CfgParseTests) and pinned the same way: an ORACLE test for the old
    // behaviour so the reason cannot be forgotten, plus the contract of the replacement.
    public class MemberReadTests
    {
        class Host
        {
            public bool FlagTrue = true;
            public bool FlagFalse = false;
            public float Angle = 12.5f;
            public int Count = 7;
            public long Index = 9L;
            public string NotANumber = "abc";
        }

        // ---- the oracle: WHY the old shape was dead. If .NET ever made these throw, the old code would have
        // been fine and this whole class would be unnecessary — so assert the premise rather than trusting it.
        [Fact]
        public void Oracle_ConvertOfNull_DoesNotThrow_AndYieldsTheFailureValue()
        {
            Assert.False(Convert.ToBoolean((object)null));   // the `loaded = true` default became FALSE
            Assert.Equal(0, Convert.ToInt32((object)null));
            Assert.Equal(0f, Convert.ToSingle((object)null));
            Assert.Equal(0L, Convert.ToInt64((object)null));
        }

        // ---- the fix: an ABSENT member yields the caller's fallback, whatever it is ----
        [Fact]
        public void MissingMember_ReturnsTheFallback_NotTheConvertedNull()
        {
            var h = new Host();
            Assert.True(UniversalInject.MemberBool(h, "NoSuchMember", true));      // the live bug: was false
            Assert.False(UniversalInject.MemberBool(h, "NoSuchMember", false));
            Assert.Equal(-1, UniversalInject.MemberInt(h, "NoSuchMember", -1));
            Assert.Equal(-1f, UniversalInject.MemberFloat(h, "NoSuchMember", -1f));
            Assert.Equal(-1L, UniversalInject.MemberLong(h, "NoSuchMember", -1L));
        }

        [Fact]
        public void PresentMember_Wins_OverTheFallback()
        {
            var h = new Host();
            Assert.True(UniversalInject.MemberBool(h, "FlagTrue", false));
            Assert.False(UniversalInject.MemberBool(h, "FlagFalse", true));   // present-and-false != absent
            Assert.Equal(12.5f, UniversalInject.MemberFloat(h, "Angle", -1f));
            Assert.Equal(7, UniversalInject.MemberInt(h, "Count", -1));
            Assert.Equal(9L, UniversalInject.MemberLong(h, "Index", -1L));
        }

        [Fact]
        public void NullHost_IsAbsent_NotACrash()
        {
            Assert.True(UniversalInject.MemberBool(null, "FlagTrue", true));
            Assert.Equal(42, UniversalInject.MemberInt(null, "Count", 42));
        }

        // A member that exists but cannot be converted is also "no" — the fallback, never a half-read value.
        [Fact]
        public void UnconvertibleMember_FallsBack()
        {
            var h = new Host();
            Assert.Equal(-1, UniversalInject.MemberInt(h, "NotANumber", -1));
            Assert.False(UniversalInject.TryMemberFloat(h, "NotANumber", out _));
        }

        // ---- the Try* pair: for call sites whose intent is "cannot read it -> leave the thing ALONE".
        // Those used `catch { continue; }`, which never fired either; in the muzzle loop an unreadable
        // SkeletonBoneIndex became 0, a VALID index, and the slot got its angle stomped instead of skipped.
        [Fact]
        public void TryMember_ReportsPresence_SoCallersCanSkip()
        {
            var h = new Host();
            Assert.False(UniversalInject.TryMemberLong(h, "NoSuchMember", out long _));
            Assert.True(UniversalInject.TryMemberLong(h, "Index", out long idx));
            Assert.Equal(9L, idx);

            Assert.False(UniversalInject.TryMemberFloat(h, "NoSuchMember", out float _));
            Assert.True(UniversalInject.TryMemberFloat(h, "Angle", out float ang));
            Assert.Equal(12.5f, ang);
        }

        // Zero is a legitimate VALUE, and must be distinguishable from "absent" — the distinction the old shape
        // could not express at all.
        [Fact]
        public void PresentZero_IsNotTheSameAsAbsent()
        {
            var h = new Host { Angle = 0f };
            Assert.True(UniversalInject.TryMemberFloat(h, "Angle", out float v));
            Assert.Equal(0f, v);
            Assert.Equal(0f, UniversalInject.MemberFloat(h, "Angle", 99f));      // present 0 beats the fallback
            Assert.Equal(99f, UniversalInject.MemberFloat(h, "Gone", 99f));      // absent yields the fallback
        }
    }
}
