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

    ///<summary>
    /// effects_new 补丁跳过的路径前缀。这些路径下的 .epk 文件不能被修改。
    /// </summary>
    public static readonly string[] EffectNewProtectedPrefixes = {
        //Viper Azmeri 相关特效：混沌矛充能、死亡溶解、浮现、待机、精灵出现（熊、猫、灵长类）、受害者血液等。Viper Azmeri 是 Act 3 第四章 的一个 Boss 或特殊怪物，这些是其技能和状态的特效。
        "metadata/effects/spells/monsters_effects/act3_four/viperazmeri/",
        
        //裂隙（Breach）联赛
        "metadata/effects/spells/monsters_effects/breach/fire/hellscapepaleelite1/",
        //"metadata/effects/spells/monsters_effects/breach/fire/hellscapepaleelite2/",
        //"metadata/effects/spells/monsters_effects/breach/lightning/demonicspikethrower/",
        //"metadata/effects/spells/monsters_effects/breach/lightning/lightning_souleater/",
        
        //深渊
        //"metadata/effects/spells/monsters_effects/league_abyss/general_effects/kurgals_gasp/",
        //"metadata/effects/spells/monsters_effects/league_abyss/kulemak/",
        //"metadata/effects/spells/monsters_effects/league_abyss/paleelite/",
        "metadata/effects/spells/monsters_effects/league_abyss/",
      
        //苦难（Affliction）联赛 装饰物（doodads）的淡入/淡出特效。
        //"metadata/effects/spells/monsters_effects/league_affliction/affliction_mods/",
        
        //Azmeri 联赛（可能为新赛季内容）特效：涉及 voodoo_king_boss（巫毒王）的大量技能（死亡标记、青蛙膨胀、传送、缓慢衰减、地面符文等）、crazedcannibalpicts_female（疯狂食人女）的武器挥动、fallenstag（堕落雄鹿）的眼睛发光、pictfemalestaff（女巫法杖）的蓄力施法、screecherminiboss（尖叫者小Boss）的传送，以及多种资源类特效（电荷层、引导光、怪物消散等）
        // "metadata/effects/spells/monsters_effects/league_azmeri/monster_fx/vodoo_king_boss/",
        // "metadata/effects/spells/monsters_effects/league_azmeri/monsters/crazedcannibalpicts_female/",
        // "metadata/effects/spells/monsters_effects/league_azmeri/monsters/fallenstag/",
        // "metadata/effects/spells/monsters_effects/league_azmeri/monsters/pictfemalestaff/",
        // "metadata/effects/spells/monsters_effects/league_azmeri/monsters/pictmaleaxedagger/",
        // "metadata/effects/spells/monsters_effects/league_azmeri/monsters/screecherminiboss/",
        // "metadata/effects/spells/monsters_effects/league_azmeri/resources/charges/",
        // "metadata/effects/spells/monsters_effects/league_azmeri/resources/guiding_light/",
        // "metadata/effects/spells/monsters_effects/league_azmeri/resources/monster_dust/",        
        "metadata/effects/spells/monsters_effects/league_azmeri/",

        //祭祀（Ritual）联赛 特效：包括 aoifeviridi（艾菲·维里迪）的消失/出现、demonrhoa（恶魔恐鸟）眼睛发光、funguszombietreehollow（真菌僵尸树）蓄力、生命祭祀的血池、各种 omen（预兆）激活、多种 ritual_rune（祭祀符文）类型（混沌、冰、火、闪电、物理、瓦尔等）的淡入淡出，以及林地祭祀的激活、聚集、准备等状态。
        // "metadata/effects/spells/monsters_effects/league_ritual/aoifeviridi_disappearing/",
        // "metadata/effects/spells/monsters_effects/league_ritual/demonfaction/demonrhoa/",
        // "metadata/effects/spells/monsters_effects/league_ritual/druidic_faction/funguszombietreehollow/",
        // "metadata/effects/spells/monsters_effects/league_ritual/life_ritual/",
        // "metadata/effects/spells/monsters_effects/league_ritual/omens/",
        // "metadata/effects/spells/monsters_effects/league_ritual/ritual_rune/",
        // "metadata/effects/spells/monsters_effects/league_ritual/ritual_vaal/",
        // "metadata/effects/spells/monsters_effects/league_ritual/woods/",
        "metadata/effects/spells/monsters_effects/league_ritual/",

        //怪物词缀 相关：主要是 tormented_spirits（被附身的幽灵）及其变体（fox_spirit、primate_spirit、spirit_animals、touched）的升级特效（普通→魔法→稀有→传奇，以及 primal/sacred/vivid/wild 不同元素的死亡/激活特效）。这些是怪物被特定词缀影响时的视觉表现。
        "metadata/effects/spells/monsters_effects/monster_mods/tormented_spirits/fox_spirit/",
        "metadata/effects/spells/monsters_effects/monster_mods/tormented_spirits/possession/",
        "metadata/effects/spells/monsters_effects/monster_mods/tormented_spirits/primate_spirit/",
        "metadata/effects/spells/monsters_effects/monster_mods/tormented_spirits/spirit_animals/",
        "metadata/effects/spells/monsters_effects/monster_mods/tormented_spirits/spirit_of_the_serpent/",
        "metadata/effects/spells/monsters_effects/monster_mods/tormented_spirits/touched/",
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

    /// <summary>
    /// 测试补丁覆盖的 9 个 metadata 二级子目录前缀。
    /// 来源：tinybundle_analysis.tsv 中所有路径的 2 级目录前缀分布。
    /// </summary>
    public static readonly string[] TestTargetPrefixes =
    {
        "metadata/effects/",
        "metadata/monsters/",
        "metadata/terrain/",
        "metadata/items/",
        "metadata/particles/",
        "metadata/critters/",
        "metadata/characters/",
        "metadata/shrines/",
        "metadata/miscellaneousobjects/",
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
        new PatchInfo { Id = PatchId.DisableSounds, Name = "disable-sounds", DisplayName = "静音", Description = "清空 SoundEvents/SoundParams 块实现静音。" },
        new PatchInfo { Id = PatchId.SkillSounds, Name = "skill-sounds", DisplayName = "技能音效", Description = "静音技能特效音效（清空 SoundEvents/SoundParams）。" },
        new PatchInfo { Id = PatchId.MonsterSounds, Name = "monster-sounds", DisplayName = "怪物音效", Description = "静音怪物音效（清空 SoundEvents/SoundParams）。" },
        new PatchInfo { Id = PatchId.MtxSoft, Name = "mtx-soft", DisplayName = "微交易软化", Description = "清空微交易特效/粒子文件(可能影响部分角色人物技能)。" },
        new PatchInfo { Id = PatchId.Blanket, Name = "blanket", DisplayName = "地毯式", Description = "激进地毯式补丁：清空 metadata/ 下所有 .epk 并简化所有 .ao。" },
        
        new PatchInfo { Id = PatchId.Effects, Name = "effects", DisplayName = "特效", Description = "剥离较多客户端特效(人物怪物均有)。" },
        new PatchInfo { Id = PatchId.Effects_New, Name = "effects-new", DisplayName = "特效(怪物)", Description = "剥离较多客户端特效(主要怪物)。" },
        new PatchInfo { Id = PatchId.Test, Name = "test", DisplayName = "特效(精准)", Description = "精准清理特效文件(人物怪物均有,比默认少)。" },
        new PatchInfo { Id = PatchId.EffectNone, Name = "effect-none", DisplayName = "不处理特效", Description = "不清理任何特效文件。" },
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
            case PatchId.Effects_New:
                return StartsWithPathCi(path, "metadata/effects/spells/monsters_effects")
                    && EndsWithPathCi(path, ".epk");
            case PatchId.Test:
                return IsTestTarget(path);
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
            case PatchId.Effects_New:
               return StartsWithPathCi(path, "metadata/effects/spells/monsters_effects")
                    && EndsWithPathCi(path, ".epk");
            case PatchId.Test:
                return IsTestTarget(path);
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

    /// <summary>
    /// 测试补丁路径匹配：仅匹配 TestTargetPaths.Set 中的精确路径（与 TinyBundle TSV 完全一致）。
    /// 不做启动场景保护排除，以便与 TinyBundle TSV（含 characterselection 路径）精确对比。
    /// 路径比较不区分大小写；输入路径中的反斜杠会被规范化为正斜杠。
    /// </summary>
    public static bool IsTestTarget(string path)
    {
        var normalized = path.Replace('\\', '/');
        return TestTargetPaths.Set.Contains(normalized);
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
