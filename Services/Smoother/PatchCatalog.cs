using System.Collections.Generic;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// 泥人补丁元数据、预设、路径选择规则。
/// 移植自 tiny-poe2smoother/src/patches.rs 的元数据和路径规则部分。
/// </summary>
public static class PatchCatalog
{
    #region 保护路径前缀

    /// <summary>
    /// particles 补丁跳过的路径前缀。这些路径下的 .pet/.trl 文件不能被清空。
    /// </summary>
    public static readonly string[] ParticleProtectedPrefixes =
    {
        "metadata/particles/monster_effects/league_legion/rewardsystem",
        "metadata/particles/monster_effects/league_legion/endgame",
        "metadata/particles/monster_effects/league_delve/general",
        "metadata/particles/monster_effects/atlasexiles/adjudicator",
        "metadata/particles/monster_effects/atlasexiles/adjudicatormonsters",
        "metadata/particles/enviro_effects/act3/blood_temple",
        "metadata/particles/ground_effects_v2/smoke_blind_chimera",
        "metadata/particles/monster_effects/atlasofworldsbosses/chimera",
        "metadata/particles/monster_effects/atlasexiles/orion",
    };

    /// <summary>
    /// effects/sound 补丁跳过的路径前缀。这些路径下的 .ao/.aoc 文件不能被修改。
    /// </summary>
    public static readonly string[] EffectProtectedPrefixes =
    {
        "metadata/effects/spells/monsters_effects/league_expedition/dynamic_marker",
        "metadata/effects/spells/monsters_effects/atlasofworldsbosses",
        "metadata/effects/spells/monsters_effects/league_azmeri/guiding_light",
        "metadata/effects/spells/monsters_effects/league_azmeri/monster_fx",
        "metadata/effects/spells/monsters_effects/league_azmeri/resources/affecting_area",
        "metadata/effects/spells/monsters_effects/league_azmeri/resources/feature_room_dust",
        "metadata/effects/spells/monsters_effects/league_azmeri/resources/guiding_light",
        "metadata/effects/spells/monsters_effects/league_azmeri/resources/wisp_doodads",
        "metadata/effects/spells/monsters_effects/league_legion/rewardsystem",
        "metadata/effects/spells/monsters_effects/league_blight/rewardsystem",
        "metadata/effects/spells/monsters_effects/league_archnemesis",
        "metadata/effects/spells/monsters_effects/league_ritual/cold_ritual",
        "metadata/effects/spells/monsters_effects/league_ultimatum/mechanics/fx/arena_limit.pet",
        "metadata/effects/spells/monsters_effects/league_sanctum",
        "metadata/effects/spells/monsters_effects/league_hellscape/mechanics",
        "metadata/effects/spells/monsters_effects/atlasofworldsbosses/maven",
        "metadata/effects/spells/monsters_effects/atlasexiles/adjudicator",
        "metadata/effects/spells/ground_effects/chimera_smoke",
        "metadata/effects/spells/ground_effects/evil",
        "metadata/effects/spells/ground_effects_v2/smoke_blind_chimera",
        "metadata/effects/spells/monsters_effects/atlasofworldsbosses/chimera",
        "metadata/effects/spells/monsters_effects/atlasexiles/orion",
        "metadata/effects/spells/monsters_effects/prophecy_league",
        "metadata/effects/spells/ground_effects/caustic",
        "metadata/effects/spells/ground_effects_v2/caustic_arrow_ground",
        "metadata/effects/spells/ground_effects_v2/desecrated",
        "metadata/effects/spells/ground_effects_v2/desecrated_maligaro",
        "metadata/effects/spells/ground_effects_v2/desecrated_red",
        "metadata/effects/spells/ground_effects_v3/caustic",
    };

    /// <summary>
    /// 启动场景保护路径前缀。这些路径下的文件不能被修改。
    /// </summary>
    public static readonly string[] StartupSceneProtectedPrefixes =
    {
        "metadata/terrain/characterselection",
        "metadata/environment/characterselection",
        "metadata/doodads/characterselection",
        "metadata/materials/characterselection",
        "metadata/effects/characterselection",
    };

    #endregion

    #region 元数据

    public static IReadOnlyList<PatchInfo> AllPatches { get; } = new[]
    {
        new PatchInfo { Id = PatchId.Camera, Name = "camera", Description = "Adjust camera zoom and remove camera reset calls." },
        new PatchInfo { Id = PatchId.Minimap, Name = "minimap", Description = "Reveal more of the minimap by default." },
        new PatchInfo { Id = PatchId.AtlasFog, Name = "atlas-fog", Description = "Remove Atlas fog of war graph nodes." },
        new PatchInfo { Id = PatchId.Fog, Name = "fog", Description = "Disable environment fog." },
        new PatchInfo { Id = PatchId.Rain, Name = "rain", Description = "Set rain intensity to zero." },
        new PatchInfo { Id = PatchId.Clouds, Name = "clouds", Description = "Set cloud intensity to zero." },
        new PatchInfo { Id = PatchId.EnvParticles, Name = "env-particles", Description = "Disable environment particles and related effects." },
        new PatchInfo { Id = PatchId.Shadow, Name = "shadow", Description = "Disable shadows in environment settings." },
        new PatchInfo { Id = PatchId.Light, Name = "light", Description = "Disable selected environment lighting systems." },
        new PatchInfo { Id = PatchId.Delirium, Name = "delirium", Description = "Disable delirium/affliction environment effects." },
        new PatchInfo { Id = PatchId.Particles, Name = "particles", Description = "Blank particle effect files." },
        new PatchInfo { Id = PatchId.Effects, Name = "effects", Description = "Strip nonessential client effect blocks." },
        new PatchInfo { Id = PatchId.DisableSounds, Name = "disable-sounds", Description = "Silence sounds by emptying SoundEvents/SoundParams blocks." },
        new PatchInfo { Id = PatchId.SkillSounds, Name = "skill-sounds", Description = "Silence skill-effect sounds (empty SoundEvents/SoundParams)." },
        new PatchInfo { Id = PatchId.MonsterSounds, Name = "monster-sounds", Description = "Silence monster sounds (empty SoundEvents/SoundParams)." },
        new PatchInfo { Id = PatchId.MtxSoft, Name = "mtx-soft", Description = "Blank microtransaction effect/particle files." },
        new PatchInfo { Id = PatchId.Blanket, Name = "blanket", Description = "Aggressive blanket patch: empty ALL .epk and simplify ALL .ao across metadata/ (matches third-party TinyBundle coverage)." },
    };

    public static IReadOnlyList<PresetInfo> AllPresets { get; } = new[]
    {
        new PresetInfo
        {
            Name = "maps-revealed",
            Description = "Reveal minimap and Atlas fog.",
            Patches = new[] { PatchId.Minimap, PatchId.AtlasFog },
        },
        new PresetInfo
        {
            Name = "performance",
            Description = "Balanced visual cleanup for performance.",
            Patches = new[] { PatchId.Fog, PatchId.Rain, PatchId.Clouds, PatchId.EnvParticles, PatchId.Delirium, PatchId.Particles, PatchId.Effects },
        },
        new PresetInfo
        {
            Name = "optimal",
            Description = "Safe recommended mix of map and environment patches.",
            Patches = new[] { PatchId.Minimap, PatchId.AtlasFog, PatchId.Fog, PatchId.Rain, PatchId.Clouds, PatchId.EnvParticles, PatchId.Effects },
        },
        new PresetInfo
        {
            Name = "daylight",
            Description = "Remove darkness, fog, shadows, and heavy environment particles.",
            Patches = new[] { PatchId.Fog, PatchId.Shadow, PatchId.Light, PatchId.EnvParticles, PatchId.Delirium },
        },
        new PresetInfo
        {
            Name = "high-performance",
            Description = "Aggressive performance preset with effects, particles, sounds, and MTX reduced.",
            Patches = new[] { PatchId.Fog, PatchId.Rain, PatchId.Clouds, PatchId.EnvParticles, PatchId.Delirium, PatchId.Particles, PatchId.Effects, PatchId.DisableSounds, PatchId.MtxSoft, PatchId.Blanket },
        },
        new PresetInfo
        {
            Name = "check-all",
            Description = "Select every ported patch.",
            Patches = new[]
            {
                PatchId.Camera, PatchId.Minimap, PatchId.AtlasFog,
                PatchId.Fog, PatchId.Rain, PatchId.Clouds, PatchId.EnvParticles,
                PatchId.Shadow, PatchId.Light, PatchId.Delirium,
                PatchId.Particles, PatchId.Effects,
                PatchId.DisableSounds, PatchId.SkillSounds, PatchId.MonsterSounds,
                PatchId.MtxSoft,
                PatchId.Blanket,
            },
        },
    };

    public static PatchId? ParsePatch(string name)
    {
        if (name.Equals("zero-particles", StringComparison.OrdinalIgnoreCase))
        {
            return PatchId.Particles;
        }
        foreach (var p in AllPatches)
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Id;
            }
        }
        return null;
    }

    public static PresetInfo? ParsePreset(string name)
    {
        foreach (var p in AllPresets)
        {
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }
        return null;
    }

    public static PatchInfo? GetPatchInfo(PatchId id)
    {
        foreach (var p in AllPatches)
        {
            if (p.Id == id) return p;
        }
        return null;
    }

    public static string PatchLabel(PatchId id) => GetPatchInfo(id)?.Name ?? "unknown";

    #endregion

    #region 路径选择规则

    /// <summary>
    /// 补丁是否应该选择此路径作为目标。
    /// 对应 Rust: patch_targets_path(patch, path)。
    /// </summary>
    public static bool PatchTargetsPath(PatchId patch, string path)
    {
        switch (patch)
        {
            case PatchId.Camera:
                return StartsWithPathCi(path, "metadata/")
                    && (EndsWithPathCi(path, ".ot") || EndsWithPathCi(path, ".otc"));
            case PatchId.Minimap:
            case PatchId.AtlasFog:
                foreach (var target in ExactPatchTargets(patch))
                {
                    if (EqPathCi(path, target)) return true;
                }
                return false;
            case PatchId.Fog:
            case PatchId.Rain:
            case PatchId.Clouds:
            case PatchId.EnvParticles:
            case PatchId.Shadow:
            case PatchId.Light:
                return StartsWithPathCi(path, "metadata/environmentsettings")
                    && EndsWithPathCi(path, ".env");
            case PatchId.Delirium:
                return StartsWithPathCi(path, "metadata/effects/environment/league_affliction")
                    && (EndsWithPathCi(path, ".ao") || EndsWithPathCi(path, ".aoc"));
            case PatchId.Particles:
                return StartsWithPathCi(path, "metadata/particles")
                    && (EndsWithPathCi(path, ".pet") || EndsWithPathCi(path, ".trl"));
            case PatchId.Effects:
                return StartsWithPathCi(path, "metadata/effects/spells")
                    && (EndsWithPathCi(path, ".aoc") || EndsWithPathCi(path, ".ao"));
            case PatchId.DisableSounds:
                return IsSoundTarget(path);
            case PatchId.SkillSounds:
                return StartsWithPathCi(path, "metadata/effects/spells")
                    && !StartsWithPathCi(path, "metadata/effects/spells/monsters_effects")
                    && IsMetadataAnimExt(path);
            case PatchId.MonsterSounds:
                return (StartsWithPathCi(path, "metadata/effects/spells/monsters_effects")
                        && IsMetadataAnimExt(path))
                    || (StartsWithPathCi(path, "metadata/monsters")
                        && IsMetadataAnimExt(path));
            case PatchId.MtxSoft:
                return StartsWithPathCi(path, "metadata/effects/microtransactions")
                    && IsMetadataEffectExt(path);
            case PatchId.Blanket:
                // 地毯式：覆盖整个 metadata/ 下的 .epk 和 .ao（但保护启动场景）。
                return StartsWithPathCi(path, "metadata/")
                    && (EndsWithPathCi(path, ".epk") || EndsWithPathCi(path, ".ao"));
            default:
                return false;
        }
    }

    /// <summary>
    /// 补丁是否对此路径执行变换。
    /// 对应 Rust: patch_applies_path(patch, path)。
    /// </summary>
    public static bool PatchAppliesPath(PatchId patch, string path)
    {
        switch (patch)
        {
            case PatchId.Camera:
                return EndsWithPathCi(path, ".ot") || EndsWithPathCi(path, ".otc");
            case PatchId.Minimap:
                return EndsWithPathCi(path, "minimap_visibility_pixel.hlsl")
                    || EndsWithPathCi(path, "minimap_blending_pixel.hlsl");
            case PatchId.AtlasFog:
                return EqPathCi(path, "metadata/materials/environment/worldmap/worldmap_fogofwar.fxgraph");
            case PatchId.Fog:
            case PatchId.Rain:
            case PatchId.Clouds:
            case PatchId.EnvParticles:
            case PatchId.Shadow:
            case PatchId.Light:
                return StartsWithPathCi(path, "metadata/environmentsettings")
                    && EndsWithPathCi(path, ".env");
            case PatchId.Delirium:
                return StartsWithPathCi(path, "metadata/effects/environment/league_affliction")
                    && (EndsWithPathCi(path, ".ao") || EndsWithPathCi(path, ".aoc"));
            case PatchId.Particles:
                return StartsWithPathCi(path, "metadata/particles")
                    && (EndsWithPathCi(path, ".pet") || EndsWithPathCi(path, ".trl"));
            case PatchId.Effects:
                return StartsWithPathCi(path, "metadata/effects/spells")
                    && (EndsWithPathCi(path, ".aoc") || EndsWithPathCi(path, ".ao"));
            case PatchId.DisableSounds:
                return IsSoundTarget(path);
            case PatchId.SkillSounds:
                return StartsWithPathCi(path, "metadata/effects/spells")
                    && !StartsWithPathCi(path, "metadata/effects/spells/monsters_effects")
                    && IsMetadataAnimExt(path);
            case PatchId.MonsterSounds:
                return (StartsWithPathCi(path, "metadata/effects/spells/monsters_effects")
                        && IsMetadataAnimExt(path))
                    || (StartsWithPathCi(path, "metadata/monsters")
                        && IsMetadataAnimExt(path));
            case PatchId.MtxSoft:
                return StartsWithPathCi(path, "metadata/effects/microtransactions")
                    && IsMetadataEffectExt(path);
            case PatchId.Blanket:
                // 地毯式：与 PatchTargetsPath 一致，覆盖整个 metadata/ 下的 .epk 和 .ao。
                return StartsWithPathCi(path, "metadata/")
                    && (EndsWithPathCi(path, ".epk") || EndsWithPathCi(path, ".ao"));
            default:
                return false;
        }
    }

    /// <summary>
    /// 精确路径目标（非广播扫描）。
    /// 对应 Rust: exact_patch_targets(patch)。
    /// </summary>
    public static string[] ExactPatchTargets(PatchId patch)
    {
        switch (patch)
        {
            case PatchId.Minimap:
                return new[]
                {
                    "shaders/minimap_visibility_pixel.hlsl",
                    "shaders/minimap_blending_pixel.hlsl",
                };
            case PatchId.AtlasFog:
                return new[] { "metadata/materials/environment/worldmap/worldmap_fogofwar.fxgraph" };
            default:
                return Array.Empty<string>();
        }
    }

    public static bool IsMetadataEffectExt(string path)
    {
        return EndsWithPathCi(path, ".ao")
            || EndsWithPathCi(path, ".aoc")
            || EndsWithPathCi(path, ".pet")
            || EndsWithPathCi(path, ".epk")
            || EndsWithPathCi(path, ".trl");
    }

    public static bool IsMetadataAnimExt(string path)
    {
        return EndsWithPathCi(path, ".ao")
            || EndsWithPathCi(path, ".aoc")
            || EndsWithPathCi(path, ".ot")
            || EndsWithPathCi(path, ".otc");
    }

    public static bool IsSoundTarget(string path)
    {
        if (IsStartupSceneProtected(path)) return false;
        return (StartsWithPathCi(path, "metadata/effects")
                || StartsWithPathCi(path, "metadata/characters")
                || StartsWithPathCi(path, "metadata/monsters")
                || StartsWithPathCi(path, "metadata/terrain")
                || StartsWithPathCi(path, "metadata/environment"))
            && !StartsWithPathCi(path, "metadata/environmentsettings")
            && IsMetadataAnimExt(path);
    }

    public static bool IsStartupSceneProtected(string path)
    {
        var normalized = NormalizePath(path);
        // Character-selection / startup-scene assets appear under several spellings
        // (concatenated: "characterselection", underscore: "char_selection")。
        // 两者都需匹配，否则游戏启动时崩溃。
        return normalized.Contains("characterselection")
            || normalized.Contains("char_selection")
            || Array.Exists(StartupSceneProtectedPrefixes, p => normalized.StartsWith(p, StringComparison.Ordinal));
    }

    public static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant();
    }

    #endregion

    #region 路径比较辅助

    /// <summary>
    /// 路径字节相等：反斜杠视为正斜杠，ASCII 大小写不敏感。
    /// 对应 Rust: path_byte_eq(a, b)。
    /// </summary>
    private static bool PathByteEq(char a, char b)
    {
        var na = a == '\\' ? '/' : char.ToLowerInvariant(a);
        var nb = b == '\\' ? '/' : char.ToLowerInvariant(b);
        return na == nb;
    }

    public static bool EqPathCi(string path, string pattern)
    {
        if (path.Length != pattern.Length) return false;
        for (var i = 0; i < path.Length; i++)
        {
            if (!PathByteEq(path[i], pattern[i])) return false;
        }
        return true;
    }

    public static bool StartsWithPathCi(string path, string prefix)
    {
        if (path.Length < prefix.Length) return false;
        for (var i = 0; i < prefix.Length; i++)
        {
            if (!PathByteEq(path[i], prefix[i])) return false;
        }
        return true;
    }

    public static bool EndsWithPathCi(string path, string suffix)
    {
        if (path.Length < suffix.Length) return false;
        var offset = path.Length - suffix.Length;
        for (var i = 0; i < suffix.Length; i++)
        {
            if (!PathByteEq(path[offset + i], suffix[i])) return false;
        }
        return true;
    }

    #endregion
}
