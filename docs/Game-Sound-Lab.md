# Game Sound Lab — audio overrides + audition

A toolchain for **replacing the game's own audio**: silence vanilla Wwise events you don't want, and (in-game) audition
any event by name so you can hear it first. Aimed at total-overhaul mods that want to reshape the soundscape; for ENC
itself it's mostly a diagnostic/authoring aid. Operates at the **event** level (see Limitations).

## Parts

| Part | Where | What |
|---|---|---|
| **Game Sound Lab** window | ENCReload editor, `Tools/HAF/Game Sound Lab` | authors `enc_sounds.json` — a list of overrides; has a searchable catalog pick list with category tabs |
| `enc_sounds.json` | `BepInEx/config/` | `{ "overrides": [ { "silence": "<event-substring>", "replaceWith": "" } ] }` — the registry (via `SoundOverrideRegistry`) |
| Plugin read | `UniversalInject.ShouldSilenceEvent` / `EnsureSoundOverrides` | drops any Wwise event whose name contains a `silence` substring, at the `AudioManager.PostEvent` service sink (`Hk_SilenceEvents`) |
| `Audio/SilenceAudioEvents` config | `BepInEx/config/community.humankind.haf.cfg` | the same silence mechanism as a hand-edit escape hatch (comma-separated substrings) |
| **F8 audition** | plugin F8 window — `Play Event` / `Stop` | post any event by name on live emitters so you can HEAR it; `Stop` halts a looping audition |
| Catalog | `Dump Sound Catalog` (F8) → `enc_sound_catalog.txt` | the game's full list of Wwise event names; feeds the Lab's pick list |

## Workflow

1. **In-game:** `F8 → Dump Sound Catalog` (in a loaded save, so the catalog is full) → writes `enc_sound_catalog.txt`.
2. **Audition (optional):** `F8 → Play Event`, type/paste an event name, hear it. `Stop` ends loops. This is how you
   identify *which* event is the one you want.
3. **Author:** open the **Game Sound Lab**, `Browse sound catalog` → search (filter by category tab) → click an event
   to add it as an override → optionally trim to a substring (drop `_Start`/`_Stop` to catch a whole family) → **Save**.
4. **Apply:** relaunch. The load log shows `[Audio] sound overrides: N silence rule(s) from enc_sounds.json`.

## How the audition works (hard-won)

Playing a Wwise *event* is nothing like playing a WAV (the `Play Sound Test (WAV)` button is a Unity `AudioSource`).
An event needs the Wwise runtime + a registered game object. What actually works:

- Look up the **`AudioEventHandle` object** by name (`Resources.FindObjectsOfTypeAll`) and post it via the **emitter's
  own `PostEvent(AudioEventHandle)`** — NOT `AkSoundEngine.PostEvent(string, gid)`, which posts silently.
- Post on **all `AudioEmitter` components** (units *and* cities/districts). `Camera.main` is null in-game so distance
  heuristics don't work, and a city-ambience event needs the **city's** emitter, not a unit's.
- A `_Start` event begins a **loop** until its `_Stop`; the `Stop` button `StopAll`s every emitter to cut it.

## Limitations (the granularity ceiling)

- **Event-level only.** Everything here addresses Wwise **events** (`Play_HG_ENV_City_BaseLayer`). The individual
  **samples** inside an event (the actual `.wem` files) are **hashed, unnamed** on disk — there is no "cart" or "birds"
  to list or silence. You can mute/audition a whole event, never one sample within it.
- **Era/context-switched content.** One event can play different audio by a Wwise **switch**. E.g.
  `Play_HG_ENV_City_BaseLayer` plays **modern** city noise for a modern empire and **ancient** village/cart ambience for
  a frozen ancient city-state — the game already does era-appropriate ambience natively, from one event, via the switch.
  Silencing the event hits all eras/empires; you can't target one switch branch by name.
- Sample-level control ("show me the cart, let me replace just it") would require **decoding the Wwise banks** — a large
  reverse-engineering effort, deliberately not attempted.

## The city-ambience investigation (shelved, low priority)

A recurring "cart sound near a modern-era map" was chased at length. Findings: it is **not** a mod leak (verified by an
A/B against a pre-audio-override plugin build — identical behaviour — and it plays across saves independent of our
units). Best-fitting explanation: a vanilla **ambient enrichment pool** — occasional, timer-driven background flavor
one-shots, context-gated by era — of which the cart is one sample inside the city-ambience event. Not confirmed to a
single named event (the definitive test, an Audio Trace parked on the ancient city-state, was not run). **Shelved as
low priority.** If revisited: trace the ancient settlement directly for the event that fires with the cart.

## Future

`replaceWith` is authored + stored but not yet consumed — the reserved hook for **silence-then-substitute** (drop the
vanilla event, post a better one). That's the natural next step for a soundscape-overhaul mod.
