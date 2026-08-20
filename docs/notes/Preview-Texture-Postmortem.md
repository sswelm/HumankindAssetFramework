# Postmortem — the preview that lied for six weeks

> **📁 ARCHIVED NOTE — frozen 2026-08-19, not maintained.** A postmortem on a fixed bug, kept for its lessons about
> why a defect survives repeated fixes. **Current texture reference: [Textures.md](../Textures.md).**

**The bug:** selecting a multi-material model in the Model Factory showed it mis-textured (scrambled/checkerboard
skin) until the next bake "fixed" it. First reported as preview weirdness in July, investigated 2026-08-01,
partially patched, and finally killed 2026-08-18. The user's own words at the end: *"my number one problem with
this editor that is still unresolved."* This note records **why it survived every attempt** — the mechanisms that
protected it are general, and worth recognizing next time.

## The root cause (one sentence)

`BuildMultiAtlasAndRemap` remapped the rig FBX's mesh UVs into the packed atlas **by mutating the imported asset
in memory** — correct until Unity reimported the FBX or restarted, at which point the FBX silently reverted to
its original UVs while the preview material kept the packed atlas: mismatched pairing, scrambled skin.

## Why it survived so long — six protective mechanisms

1. **It had an alibi: it self-healed after every bake.** The natural workflow is select → tweak → **bake** → look.
   Every session *ended* with a correct preview, so every investigation *started* from a state where the bug was
   invisible. It only showed at the moment you were least likely to be debugging — a fresh session's first click.

2. **The broken state lived nowhere on disk.** Nothing in any file was wrong — the atlas was correct, the FBX was
   correct, the persisted preview mesh was correct. The *pairing* was wrong, and only in transient editor memory.
   No diff, no dump, no file inspection could show it; the 08-01 investigation's `draw_mats.txt` proved the
   texture innocent and still couldn't see that the *mesh* was the variable that changed between sessions.

3. **Every symptom had a plausible wrong explanation.** DXT compression, stale import caches, a display-only
   atlas quirk — the recorded 08-01 "long false trail" (ForceUpdate reimport → RGBA32 blit copy → runtime
   material instance) chased the texture three different ways. All three fixes were aimed at the innocent half of
   the pairing, so all three failed in ways that *looked like the bug was mysterious* rather than misdiagnosed.

4. **A partial fix hid the pressure without touching the cause.** The 08-01 patch (↻ Reload keeps the existing
   preview instead of rebuilding) suppressed the most common *trigger* — and the "real fix (a preview mesh
   carrying atlas UVs)" note sank into the backlog. A symptom suppressed is a priority halved: the bug stopped
   being encountered daily by the person who understood it best, while still greeting every fresh session.

5. **The system grew a rule that DEPENDED on the bug.** "NEVER force-reimport the FBX" was recorded as a safety
   rule after a reimport "scrambled" the Lab preview. In truth, the reimport was *reverting the accidental
   in-memory remap* — the rule was protecting the bug's mechanism, and made the broken architecture look
   load-bearing. Protective rules that record only *what* to avoid, not *why*, entrench whatever they guard.

6. **It was triaged as cosmetic because the game was always right.** The shipped mesh carries remapped UVs
   (proven 08-01; every in-game verification since), so the bug never damaged a mod — and "display-only" issues
   lose every priority contest. But a chronic display lie poisons something worse than one model: **trust in the
   whole editor**. If the preview lies about textures, what else is it lying about? That is why a "cosmetic" bug
   was the user's #1 problem — severity was measured in data risk when the real cost was confidence.

## What finally killed it — and nearly didn't

The fix took **three versions in one evening**, and the failure modes of the first two are part of the lesson:

- **v1** preferred the bake's textured `_Preview.prefab` — texture right, but a display-flipped bind pose with no
  ground plane. *Drill-caught by the user in minutes* ("why is it heading up without a surface?"). Reverted.
- **v2** substituted the persisted remapped mesh into the FBX route, matched **by name** — which could never
  fire: `AssetDatabase.CreateAsset` renames a persisted mesh to its filename. The substitution silently did
  nothing, i.e. **the fix failed exactly like the bug: invisibly**. *Drill-caught again* ("still corrupt").
- **v3** matches by **geometry identity** (identical vertex count) and prints a loud `APPLIED` / `NO MATCH`
  Console line per preview load. *Drill-verified:* "finally it looks correct."

What broke the six-week pattern was not better debugging of symptoms — it was (a) reading the bake's mechanism
until the transient state was found, and (b) a user drilling every version immediately, per the
"[a tool is not trusted until it is DRILLED](../Decisions.md)" rule. Both earlier fix attempts would have been
declared successful under review-only.

## Lessons (each one general)

- **Correct-by-accident is a bug even while the output is correct.** State that must survive a reimport/restart
  and doesn't will eventually present as an unreproducible mystery. Persist it or don't depend on it.
- **A symptom-suppressing fix must be labelled as one, loudly,** and the real fix must keep an owner — "deferred
  to backlog" without a trigger is where chronic bugs go to become permanent.
- **Protective rules must record their WHY.** "Never reimport the FBX" pointed straight at the mechanism for
  weeks; nobody could see it because the rule only said *what*.
- **Diagnostic surfaces deserve fail-loud too.** The preview mispaired silently; the v2 fix failed silently. One
  `APPLIED / NO MATCH` log line is the difference between six weeks and six minutes.
- **"Cosmetic" is not a severity when it is chronic.** Price display bugs by the trust they burn, not the data
  they risk.

## Epilogue (2026-08-19): the fix was correct, its deployment was incomplete — twice

The day after "finally it looks correct," the user caught the **Animation Lab** showing the same corrupt pairing
on load: the substitution fix was surgical to the Factory's `LoadPreview`, and the Lab has its own *copy* of the
same renderer-flattening loop. Porting it took minutes — and then the port itself missed a call site: it landed
in the Lab's *rebuild* path while the *domain-reload restore* path (the one that actually runs right after a
compile — precisely when the user looks) still drew unsubstituted. Two more lessons for the list:

- **When a fix lands in copied code, grep for the copies.** The identical `sharedMesh`-flattening loop existed
  in two windows; fixing the one you're looking at is half a fix.
- **A fix has as many deployment points as the code has call sites** — the one-forgotten-call-site pattern (the
  same one the Factory's SelectEntry funnel was built to kill) applies to fixes too. The loud `APPLIED / NO
  MATCH` log earned its keep again: it separated "wrong path ran" from "fix is wrong" in one Console glance.
