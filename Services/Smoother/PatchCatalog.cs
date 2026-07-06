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
        //"metadata/particles/conditions/",                                              //异常状态粒子（点燃/冰冻/感电/腐蚀等）
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
        "metadata/effects/spells/ground_effects/",                                  //怪物技能环境特效，地面的火应该在这里
        "metadata/effects/spells/ground_effects_v2/",                               //怪物技能环境特效
        "metadata/effects/spells/ground_effects_v3/",                               //怪物技能环境特效
        "metadata/effects/spells/grd_zones/",                                      //地面环境特效
        "metadata/effects/spells/monsters_effects/atlasofworldsbosses/chimera",
        "metadata/effects/spells/monsters_effects/atlasexiles/orion",
        "metadata/effects/spells/monsters_effects/prophecy_league",
        "metadata/effects/spells/monsters_effects/league_delirium/tangmazu/",       //恢弘之镜系列的东西
        //"metadata/effects/spells/environment_effects/",
        "metadata/effects/spells/ground_effects_v2/caustic_arrow_ground",
        "metadata/effects/spells/ground_effects_v2/desecrated",
        "metadata/effects/spells/ground_effects_v2/desecrated_maligaro",
        "metadata/effects/spells/ground_effects_v2/desecrated_red",
        "metadata/effects/spells/ground_effects_v3/caustic",
        //"metadata/effects/spells/monsters_effects/league_delirium/fog_origin",              //雾的起源
        "metadata/effects/spells/monsters_effects/league_delirium/deliriumobject/",       //恢复弘之镜系列的东西
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
        new PatchInfo { Id = PatchId.Camera, Name = "camera", DisplayName = "相机", Description = "调整相机缩放倍率，移除相机重置调用。", IsDangerous = true },
        new PatchInfo { Id = PatchId.Minimap, Name = "minimap", DisplayName = "小地图", Description = "默认显示更多小地图区域。", IsDangerous = true },
        new PatchInfo { Id = PatchId.AtlasFog, Name = "atlas-fog", DisplayName = "异界迷雾", Description = "移除异界图集的战争迷雾图节点。", IsDangerous = true },
        new PatchInfo { Id = PatchId.Fog, Name = "fog", DisplayName = "雾", Description = "禁用环境雾。" },
        new PatchInfo { Id = PatchId.Rain, Name = "rain", DisplayName = "雨", Description = "将雨强度设为零。" },
        new PatchInfo { Id = PatchId.Clouds, Name = "clouds", DisplayName = "云", Description = "将云强度设为零。" },
        new PatchInfo { Id = PatchId.EnvParticles, Name = "env-particles", DisplayName = "环境粒子", Description = "禁用环境粒子及相关效果。" },
        new PatchInfo { Id = PatchId.Shadow, Name = "shadow", DisplayName = "阴影", Description = "在环境设置中禁用阴影。" },
        new PatchInfo { Id = PatchId.Light, Name = "light", DisplayName = "光照", Description = "禁用选定的环境光照系统。" },
        new PatchInfo { Id = PatchId.Delirium, Name = "delirium", DisplayName = "谵妄", Description = "禁用谵妄/苦难环境效果。" },
        new PatchInfo { Id = PatchId.Particles, Name = "particles", DisplayName = "粒子", Description = "清空粒子效果文件。" },
        new PatchInfo { Id = PatchId.Effects, Name = "effects", DisplayName = "特效", Description = "剥离非必要的客户端特效块。" },
        new PatchInfo { Id = PatchId.DisableSounds, Name = "disable-sounds", DisplayName = "静音", Description = "清空 SoundEvents/SoundParams 块实现静音。" },
        new PatchInfo { Id = PatchId.SkillSounds, Name = "skill-sounds", DisplayName = "技能音效", Description = "静音技能特效音效（清空 SoundEvents/SoundParams）。" },
        new PatchInfo { Id = PatchId.MonsterSounds, Name = "monster-sounds", DisplayName = "怪物音效", Description = "静音怪物音效（清空 SoundEvents/SoundParams）。" },
        new PatchInfo { Id = PatchId.MtxSoft, Name = "mtx-soft", DisplayName = "微交易软化", Description = "清空微交易特效/粒子文件。" },
        new PatchInfo { Id = PatchId.Blanket, Name = "blanket", DisplayName = "地毯式", Description = "激进地毯式补丁：清空 metadata/ 下所有 .epk 并简化所有 .ao。" },
    };

    public static IReadOnlyList<PresetInfo> AllPresets { get; } = new[]
    {
        new PresetInfo
        {
            Name = "maps-revealed",
            DisplayName = "地图全开",
            Description = "显示小地图和异界图集迷雾。",
            Patches = new[] { PatchId.Minimap, PatchId.AtlasFog },
            IsHidden = true,
        },
        new PresetInfo
        {
            Name = "performance",
            DisplayName = "性能",
            Description = "平衡的视觉清理以提升性能。",
            Patches = new[] { PatchId.Fog, PatchId.Rain, PatchId.Clouds, PatchId.EnvParticles, PatchId.Particles, PatchId.Effects },
        },
        new PresetInfo
        {
            Name = "optimal",
            DisplayName = "最优",
            Description = "地图与环境补丁的安全推荐组合。",
            Patches = new[] { PatchId.Minimap, PatchId.AtlasFog, PatchId.Fog, PatchId.Rain, PatchId.Clouds, PatchId.EnvParticles, PatchId.Effects },
            IsHidden = true,
        },
        new PresetInfo
        {
            Name = "daylight",
            DisplayName = "白昼",
            Description = "移除黑暗、雾、阴影和重型环境粒子。",
            Patches = new[] { PatchId.Fog, PatchId.Shadow, PatchId.Light, PatchId.EnvParticles, PatchId.Delirium },
            IsHidden = true,
        },
        new PresetInfo
        {
            Name = "blanket-only",
            DisplayName = "地毯式",
            Description = "仅勾选地毯式补丁：清空 metadata/ 下所有 .epk 并简化所有 .ao。",
            Patches = new[] { PatchId.Blanket },
        },
        new PresetInfo
        {
            Name = "high-performance",
            DisplayName = "高性能",
            Description = "性能预设 + 地毯式：视觉清理与激进 .epk/.ao 简化。",
            Patches = new[] { PatchId.Fog, PatchId.Rain, PatchId.Clouds, PatchId.EnvParticles, PatchId.Particles, PatchId.Effects, PatchId.Blanket },
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

    /// <summary>获取补丁的中文显示名（UI 展示用）。找不到时回退到英文 Name。</summary>
    public static string PatchDisplayName(PatchId id) => GetPatchInfo(id)?.DisplayName ?? PatchLabel(id);

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
