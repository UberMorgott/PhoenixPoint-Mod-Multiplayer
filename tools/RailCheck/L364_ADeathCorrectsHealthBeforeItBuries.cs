using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Multiplayer.Tactical;

namespace RailCheck
{
    /// <summary>
    /// L364 — THE DEATH RECORD CORRECTS HEALTH BEFORE IT KILLS, SO A BURIAL CANNOT HIDE A DIVERGENCE.
    ///
    /// Op 4 of 0x84 is the death no damage record carried — a status kill, a scripted one, a self-destruct. It
    /// shipped a key and a corpse manifest and NOTHING ELSE, so all it could do was bury: a peer whose hit
    /// points had already drifted was set to zero at the wrong number, and the difference went into the corpse
    /// unreported and unrepairable. A death is the one moment a health divergence becomes permanent, which
    /// makes it the worst possible moment to stop checking for one.
    ///
    /// ORDER IS THE ASSERTION, not merely presence. Correcting AFTER the kill writes a number onto a corpse;
    /// correcting BEFORE it is the kill — <c>BaseStat.Set</c>:95-107 →
    /// <c>TacticalActorBase.OnHealthChange</c>:616-622 → <c>Die()</c> is the game's own death trigger, so
    /// writing the host's zero IS the native death and <c>ForceDeath</c> falls back to the backstop it was
    /// always meant to be. It is also what makes <c>Correct</c>'s warning meaningful here: it fires exactly
    /// when the two peers disagreed.
    ///
    /// AND IT RETIRES A FALSE ALARM. "forcing Fireworm_19 dead at 50 HP" was read as a bug and cost an
    /// investigation; it was correct by design. The kill came from <c>PoisonwormExplode_AbilityDef</c>, a
    /// self-destruct that never enters <c>ApplyDamageInternal</c>, and 50 was full health — the actor was
    /// SUPPOSED to be at full health and SUPPOSED to die by this op. With the host's number riding along, that
    /// case now writes 0 and never reaches the warning at all.
    ///
    /// Falsify (each verified RED, then restored): move the <c>Correct</c> call after <c>ForceDeath</c> → (b)
    /// corrects-a-corpse; delete it → (b) burial-without-a-check; drop the <c>hp</c> write from the host tick
    /// → (a) host-ships-no-health; drop the <c>ReadSingle</c> → (a) address-misaligned.
    /// </summary>
    internal static class L364_ADeathCorrectsHealthBeforeItBuries
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;

        internal static IEnumerable<string> Check()
        {
            var life = typeof(TacticalActorLifecycle);
            var asm = life.Assembly;
            var apply = life.GetMethod("ApplyDeath", All);
            var hostTick = life.GetMethod("HostTick", All);
            if (apply == null || hostTick == null)
            {
                yield return "L364 premise-changed: TacticalActorLifecycle.ApplyDeath / HostTick no longer resolve. " +
                             "Op 4 is the only carrier for a death that no damage record named — a status kill, a " +
                             "scripted one, a self-destruct — so without them those deaths reach no other peer at " +
                             "all and every arm below is vacuous.";
                yield break;
            }

            // ── (a) THE HOST'S HEALTH IS ON THE WIRE, AND THE READER TAKES IT ────────
            var written = L357_TheResnapshotRepairsItemsOrSaysWhyNot.Closure(life, "HostTick")
                                                                    .SelectMany(Program.CalleeSequence).ToList();
            if (!written.Any(c => c.Name == "Stat" || c.Name == "get_Health"))
                yield return "L364 host-ships-no-health: the death op's body never reads the dying actor's health, " +
                             "so the record still carries a key and a corpse manifest and nothing that could repair " +
                             "a peer whose hit points had already drifted. That peer is buried at the wrong number " +
                             "and no later message can say so — the corpse has no health left to compare.";
            var read = Program.CalleeSequence(apply);
            int hpRead = read.FindIndex(c => c.Name == "ReadSingle");
            int lootRead = read.FindIndex(c => c.Name == "ReadLoot");
            if (hpRead < 0)
                yield return "L364 address-misaligned: ApplyDeath reads no float. If the host writes one, this does " +
                             "not merely lose the health — it shifts the corpse manifest that follows it and the " +
                             "whole record decodes as garbage, silently.";
            else if (lootRead >= 0 && hpRead > lootRead)
                yield return "L364 fields-out-of-order: ApplyDeath reads the corpse manifest before the health, but " +
                             "the host writes them the other way round. A stream read out of order is not a wrong " +
                             "value, it is a wrong parse of everything after it.";

            // ── (b) THE CORRECTION HAPPENS, AND IT HAPPENS FIRST ─────────────────────
            int corrected = read.FindIndex(c => c.Name == "Correct");
            int forced = read.FindIndex(c => c.Name == "ForceDeath");
            if (corrected < 0)
                yield return "L364 burial-without-a-check: ApplyDeath never corrects the actor's health from the " +
                             "host's. A death is the one event that makes a health divergence permanent, so this is " +
                             "the last moment such a difference can be seen at all — and it is skipped.";
            else if (forced >= 0 && corrected > forced)
                yield return "L364 corrects-a-corpse: ApplyDeath forces the death BEFORE it writes the host's " +
                             "health. Writing a number onto something already dead repairs nothing and reports " +
                             "nothing; writing it first IS the native death (Health.Set is the game's own trigger) " +
                             "and is the only ordering in which the two peers' disagreement is ever visible.";
        }
    }
}
