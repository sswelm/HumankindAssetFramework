// VehicleLabNameGuess.cs - THE ROLE THE VEHICLE LAB GUESSES FROM A PART'S NAME (a Probe's part with no session role).
// One copy: VehicleLabWindow.GuessRoleFromName calls it, and so does the Model Fuser for a part the Lab has no record
// of yet, when it decides what the unseen-faces pass may leave out (GlbDisconnectedParts.UnseenRoleFromLabRecords).
// Its own file, outside the window, so the tests compile it (2026-11-27, review of the unseen-faces pass: "propeller"
// contains "rope", and a Propeller came out Rigging - a role that stands still - instead of a rotor).
using System;

public static class VehicleLabNameGuess
{
    // VehicleLabWindow.Role by number (the recipes and role lists store the number; keep these in step with the enum)
    public const int Wheel = 1, Turret = 2, Default = 4, Rotor = 8, TailRotor = 9, Oar = 13, Sail = 14, Rigging = 15,
        Flag = 17, Rudder = 18, Shroud = 22, Window = 23, Railing = 24, Chain = 26;

    public static int RoleOf(string name)
    {
        string low = (name ?? "").ToLowerInvariant();
        return low.Contains("tail") && (low.Contains("rotor") || low.Contains("prop")) ? TailRotor  // "tail rotor" before the generic rotor guess
            : low.Contains("fantail") || low.Contains("fenestron") ? TailRotor
            : low.Contains("oar") ? Oar   // before the generic "blade" -> Rotor guess: an oar blade contains "blade"
            : low.Contains("sail") ? Sail
            : low.Contains("shroud") || low.Contains("ratline") ? Shroud   // standing rigging, before the generic rope guess
            : low.Contains("rigging") || (low.Contains("rope") && !low.Contains("propeller")) ? Rigging   // "propeller" contains "rope"
            : low.Contains("window") || low.Contains("windshield") || low.Contains("windscreen") || low.Contains("glass") ? Window   // glazing (2026-09-25)
            : low.Contains("railing") || low.Contains("handrail") || low.Contains("stanchion") ? Railing   // its own soup tier since 2026-11-12 (was Structure)
            : low.Contains("chain") ? Chain   // anchor chains, their own soup tier since 2026-11-25
            : low.Contains("flag") || low.Contains("banner") || low.Contains("pennant") ? Flag
            : low.Contains("rudder") ? Rudder
            : low.Contains("rotor") || low.Contains("helix") || low.Contains("blade") || low.Contains("propeller") ? Rotor
            : low.Contains("wheel") || low.Contains("tyre") || low.Contains("tire") ? Wheel
            : low.Contains("turret") ? Turret : Default;
    }
}
