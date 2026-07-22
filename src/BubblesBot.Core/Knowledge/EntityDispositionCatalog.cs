namespace BubblesBot.Core.Knowledge;

/// <summary>
/// How the bot should relate to a Monster-kind entity, resolved once at cache hydration
/// from its metadata path.
/// </summary>
public enum EntityDisposition
{
    /// <summary>A real fight target: counts as hostile, eligible for targeting and beacons.</summary>
    Combatant,

    /// <summary>
    /// Never target, never count as hostile, never path toward. Invisible controllers,
    /// friendly NPCs sharing Monster kind, player-owned clones, invulnerable observers.
    /// </summary>
    Ignore,

    /// <summary>
    /// Never target — attacks are wasted — but DO track: these hurt on contact/expiry
    /// (volatile orbs, on-death effects). The avoidance/dodge layer consumes these.
    /// </summary>
    Hazard,
}

/// <summary>
/// Curated library of Monster-kind entities that are not ordinary fight targets, keyed by
/// metadata-path fragment (case-insensitive substring, first match wins). Grown from live
/// damage-gate evidence: the push-mode blacklist logs the metadata path of every target
/// that soaked 700 ms of attacks without losing HP — graduate repeat offenders into this
/// table so they never enter targeting at all.
/// </summary>
public static class EntityDispositionCatalog
{
    public const string Version = "dispositions-2026-07-14.2";
    private static readonly (string Fragment, EntityDisposition Disposition)[] Rules =
    {
        // ── Ignore entirely ────────────────────────────────────────────────
        // Invisible Archnemesis/league effect controllers (ChargeGenerator, GraspingVines,
        // godmode-ghost preload, …). All of Metadata/Monsters/Daemon/* is server-side
        // effect plumbing — never a real target.
        ("/Monsters/Daemon/",       EntityDisposition.Ignore),
        // Player-mirage clones (map mods / build effects) — ride at the player's feet,
        // full HP, undamageable. Observed live 2026-07-14: RangerMirage.
        ("/Monsters/Mirage/",       EntityDisposition.Ignore),
        // The Maven WITNESSING a map — invulnerable observer hovering over the fight.
        // (Her actual boss-fight entity uses a different path and stays Combatant.)
        ("/MavenBoss/TheMavenMapVolatileOrb", EntityDisposition.Hazard),
        ("/MavenBoss/TheMavenMap",  EntityDisposition.Ignore),
        // Animated-item visual entities.
        ("/AnimatedItem/",          EntityDisposition.Ignore),
        // Friendly master NPCs that share Monster kind + Unique rarity (Einhar, Cassia,
        // Niko, Jun, Alva, Zana, Kirac, …).
        ("/Masters/",               EntityDisposition.Ignore),
        // Blight player-side structures (build pads + built towers).
        ("/LeagueBlight/BlightFoundation", EntityDisposition.Ignore),
        ("/LeagueBlight/BlightTower",      EntityDisposition.Ignore),
        // Ambient wildlife: undamageable decorations flagged Monster-kind. Observed live
        // 2026-07-14 wedging the beacon fallback (Giant Ugly Toad across an uncrossable
        // gap read as "nearest live hostile" forever).
        ("Metadata/Critters/",             EntityDisposition.Ignore),
        ("/GiantUglyToadCritter/",         EntityDisposition.Ignore),
        ("/Gruthkul/GruthkulMonkey",       EntityDisposition.Ignore),
        // NOTE: ally-class entries (Blink Arrow clones, Kitava's Innocence/Sin, shrine
        // summons…) deliberately do NOT live here. Allies are detected authoritatively via
        // the Positioned Reaction byte (Entry.IsAllied, live-verified 2026-07-14) — this
        // catalog only covers what that byte can't: neutral undamageable noise and hazards.

        // ── Avoid, don't attack ────────────────────────────────────────────
        // Archnemesis volatile cores: unkillable orbs that chase the player and explode.
        ("/Monsters/VolatileCore/", EntityDisposition.Hazard),
        // Faridun swarm ground-damage carpet — Monster-kind, undamageable, hurts to stand
        // in. Observed live 2026-07-14 ('Ichorous Pest').
        ("/FaridunSwarm/FaridunSwarmGroundEffect", EntityDisposition.Hazard),
        // Simulacrum boss-death effects masquerade as targetable, full-life monsters. Wave
        // 13 flight evidence showed these repeatedly spawning/despawning for 130+ seconds;
        // chasing them starved the arena frontier sweep until the wave timeout.
        ("/InvisibleFire/AfflictionBossFinalDeathZone", EntityDisposition.Hazard),
        ("/FinalBossDeathZones/", EntityDisposition.Hazard),
    };

    /// <summary>Classify a metadata path. Unmatched paths are ordinary combatants.</summary>
    public static EntityDisposition Classify(string metadataPath)
    {
        if (string.IsNullOrEmpty(metadataPath)) return EntityDisposition.Combatant;
        foreach (var (fragment, disposition) in Rules)
            if (metadataPath.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                return disposition;
        return EntityDisposition.Combatant;
    }

    /// <summary>Apply display-name rules for entities whose volatile implementation reuses an
    /// ordinary monster metadata template. Path rules remain authoritative when they match.</summary>
    public static EntityDisposition Classify(string metadataPath, string? displayName)
    {
        var pathDisposition = Classify(metadataPath);
        if (pathDisposition != EntityDisposition.Combatant) return pathDisposition;
        if (displayName is not null
            && (displayName.Equals("Volatile", StringComparison.OrdinalIgnoreCase)
                || displayName.Equals("Volatile Core", StringComparison.OrdinalIgnoreCase)))
            return EntityDisposition.Hazard;
        return pathDisposition;
    }
}
