using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Poe2PriceGui.Services.Smoother;

/// <summary>
/// 泥人补丁的 16 个变换实现：将原始文件字节按补丁类型转换为打补丁后的字节。
/// 移植自 tiny-poe2smoother/src/patches.rs 的 transform 系列函数。
///
/// 涉及的文件类型与编码：
/// - .env/.fxgraph/.ao/.aoc/.ot/.otc/.pet/.trl：UTF-16 LE with BOM
/// - .hlsl：UTF-8（minimap 着色器）
/// - .epk：UTF-16 LE with BOM（被清空为空字符串）
/// </summary>
internal static class PatchTransforms
{
    #region 正则与常量集合

    /// <summary>
    /// 匹配 "rain_intensity": &lt;值&gt;,
    /// 三组：前缀（含冒号和空白）、值、可选逗号。
    /// </summary>
    private static readonly Regex RainIntensityRe =
        new(@"(""rain_intensity"":\s*)([^,\r\n}]+)(,?)", RegexOptions.Compiled);

    /// <summary>
    /// 匹配 "clouds_intensity": &lt;值&gt;,
    /// </summary>
    private static readonly Regex CloudsIntensityRe =
        new(@"(""clouds_intensity"":\s*)([^,\r\n}]+)(,?)", RegexOptions.Compiled);

    /// <summary>
    /// effects 补丁在 client 块中保留的子块名。
    /// 对应 Rust: EFFECT_KEEP_BLOCKS。
    /// </summary>
    private static readonly HashSet<string> EffectKeepBlocks = new(StringComparer.Ordinal)
    {
        "ClientAnimationController",
        "SoundEvents",
        "BoneGroups",
        "AnimatedRender",
        "SkinMesh",
    };

    /// <summary>
    /// 声音补丁清空体的子块名（Name { ... } → Name {}）。
    /// 对应 Rust: SOUND_EMPTY_BLOCKS。
    /// </summary>
    private static readonly HashSet<string> SoundEmptyBlocks = new(StringComparer.Ordinal)
    {
        "SoundEvents",
        "SoundParams",
    };

    /// <summary>
    /// 地毯式补丁清空体的子块名。
    /// 清空 Lights（场景灯光）和 BaseAnimationEvents（动画事件），
    /// 保留块名只清空体（Name { ... } → Name {}）。
    ///
    /// 诊断数据（72,344 个 .ao 文件，仅计有内容的块）：
    ///   Lights:                2,040 文件
    ///   BaseAnimationEvents:   3,911 文件
    ///   合并 (Lights ∪ BAE):    5,866 文件
    ///   + .epk 8,304 = 总计 ~14,170 文件（接近别人的 15,654）
    ///
    /// 不含 SoundEvents（17,750 文件，加入后总计 30,543，远超别人）。
    /// 声音清空已由 StripSounds 补丁（performance 预设包含）独立处理。
    /// </summary>
    private static readonly HashSet<string> BlanketEmptyBlocks = new(StringComparer.Ordinal)
    {
        "Lights",
        "BaseAnimationEvents",
    };

    #endregion

    #region 主分发

    /// <summary>
    /// 主分发：根据补丁类型调用对应的变换。
    /// 对应 Rust: transform(patch, path, bytes, zoom)。
    ///
    /// 返回的 byte[] 即为变换后的内容（已包含 BOM）。
    /// </summary>
    public static byte[] Transform(PatchId patch, string path, byte[] bytes, double zoom)
    {
        switch (patch)
        {
            case PatchId.Camera:
                return Camera(path, bytes, zoom);
            case PatchId.Minimap:
                return Minimap(path, bytes);
            case PatchId.AtlasFog:
                return AtlasFog(bytes);
            case PatchId.Fog:
                return ReplaceUtf16(bytes, new[] { ("\"fog\"", "\"xog\"") });
            case PatchId.Rain:
                return RegexUtf16(bytes, RainIntensityRe, "${1}0.0${3}");
            case PatchId.Clouds:
                return RegexUtf16(bytes, CloudsIntensityRe, "${1}0.0${3}");
            case PatchId.EnvParticles:
                return EnvParticles(bytes);
            case PatchId.Shadow:
                return ReplaceUtf16(bytes, new[] { ("\"shadows_enabled\": true", "\"shadows_enabled\": false") });
            case PatchId.Light:
                return ReplaceUtf16(bytes, new[]
                {
                    ("\"directional_light\"", "\"xirectional_light\""),
                    ("\"player_light\"", "\"xlayer_light\""),
                    ("\"environment_mapping\"", "\"xnvironment_mapping\""),
                    ("\"global_illumination\"", "\"xlobal_illumination\""),
                });
            case PatchId.Delirium:
                return Delirium(bytes);
            case PatchId.Particles:
                return Particles(path, bytes);
            case PatchId.Effects:
                return Effects(path, bytes);
            case PatchId.DisableSounds:
            case PatchId.SkillSounds:
            case PatchId.MonsterSounds:
                return StripSounds(path, bytes);
            case PatchId.MtxSoft:
                return MtxSoft(path, bytes);
            case PatchId.MonsterHpBar:
                return MonsterHpBar(bytes);
            case PatchId.Blanket:
                return Blanket(path, bytes);
            case PatchId.Test:
                return TestTarget(path, bytes);
            case PatchId.Effects_New:
                return Effects_New(path, bytes);
            default:
                return bytes;
        }
    }

    #endregion

    #region Camera

    /// <summary>
    /// 相机补丁：调整 metadata/characters/character.ot 中的 CreateCameraZoomNode 参数，
    /// 其他 .ot/.otc 文件则移除 9 个相机相关函数调用。
    /// 对应 Rust: camera(path, bytes, zoom)。
    /// </summary>
    private static byte[] Camera(string path, byte[] bytes, double zoom)
    {
        var text = DecodeUtf16(bytes);
        if (path.Equals("metadata/characters/character.ot", StringComparison.OrdinalIgnoreCase))
        {
            var zoomStr = zoom.ToString("F1", CultureInfo.InvariantCulture);
            // 按 \r\n 切行处理
            var lines = text.Split("\r\n");
            var idx = -1;
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("CreateCameraZoomNode"))
                {
                    idx = i;
                    break;
                }
            }
            var newLine = $"\ton_initial_position_set = {{CreateCameraZoomNode(5000.0, 5000.0, {zoomStr});}} ";
            if (idx >= 0)
            {
                lines[idx] = newLine;
            }
            else
            {
                // 没有 CreateCameraZoomNode 行，则在 team = 1 行之后插入
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("team = 1"))
                    {
                        // 在 idx+1 处插入
                        var newLines = new List<string>(lines.Length + 1);
                        for (var j = 0; j <= i; j++) newLines.Add(lines[j]);
                        newLines.Add(newLine);
                        for (var j = i + 1; j < lines.Length; j++) newLines.Add(lines[j]);
                        lines = newLines.ToArray();
                        break;
                    }
                }
            }
            text = string.Join("\r\n", lines);
            return EncodeUtf16Bom(text);
        }

        var functions = new[]
        {
            "CreateCameraZoomNode",
            "ClearCameraZoomNodes",
            "CreateCameraLookAtNode",
            "CreateCameraPanNode",
            "ClearCameraPanNode",
            "ClearCameraPanNodes",
            "SetCustomCameraSpeed",
            "RemoveCustomCameraSpeed",
            "FaceCamera",
        };
        var hasAny = false;
        foreach (var func in functions)
        {
            if (text.Contains(func))
            {
                hasAny = true;
                break;
            }
        }
        if (!hasAny)
        {
            return bytes;
        }
        foreach (var func in functions)
        {
            text = TextBlockParser.RemoveFunctionCalls(text, func);
        }
        return EncodeUtf16Bom(text);
    }

    #endregion

    #region Minimap

    /// <summary>
    /// 小地图补丁：修改两个 minimap 像素着色器，扩大可见区域。
    /// 对应 Rust: minimap(path, bytes)。
    ///
    /// minimap_visibility_pixel.hlsl：在 res_color = float4(1.0f, 0.0f, 0.0f, 1.0f); 之后插入
    ///   res_color = max(res_color, 0.18f);
    /// minimap_blending_pixel.hlsl：替换两个颜色常量。
    /// </summary>
    private static byte[] Minimap(string path, byte[] bytes)
    {
        // 着色器是 UTF-8
        var text = Encoding.UTF8.GetString(bytes);
        if (path.EndsWith("minimap_visibility_pixel.hlsl", StringComparison.Ordinal))
        {
            if (!text.Contains("res_color = max(res_color, 0.18f);"))
            {
                var lines = text.Split("\r\n");
                for (var i = 0; i < lines.Length; i++)
                {
                    if (lines[i].Contains("res_color = float4(1.0f, 0.0f, 0.0f, 1.0f);"))
                    {
                        var newLines = new List<string>(lines.Length + 1);
                        for (var j = 0; j <= i; j++) newLines.Add(lines[j]);
                        newLines.Add("\tres_color = max(res_color, 0.18f);");
                        for (var j = i + 1; j < lines.Length; j++) newLines.Add(lines[j]);
                        lines = newLines.ToArray();
                        break;
                    }
                }
                text = string.Join("\r\n", lines);
            }
        }
        else if (path.EndsWith("minimap_blending_pixel.hlsl", StringComparison.Ordinal))
        {
            text = text
                .Replace(
                    "float4 walkable_color = float4(1.0f, 1.0f, 1.0f, 0.01f);",
                    "float4 walkable_color = float4(0.0f, 0.0f, 0.0f, 0.3f);")
                .Replace(
                    "float4 walkability_map_color = lerp(walkable_color, float4(0.5f, 0.5f, 1.0f, 0.5f), walkable_to_edge_ratio);",
                    "float4 walkability_map_color = lerp(walkable_color, float4(12.0f, 12.0f, 12.0f, 0.1f), walkable_to_edge_ratio);");
        }
        return Encoding.UTF8.GetBytes(text);
    }

    #endregion

    #region AtlasFog

    /// <summary>
    /// Atlas 雾补丁：清空 fxgraph 文件中的 nodes 和 links 数组。
    /// 对应 Rust: atlas_fog(bytes)。
    /// </summary>
    private static byte[] AtlasFog(byte[] bytes)
    {
        var text = DecodeUtf16(bytes);
        text = TextBlockParser.ReplaceArrayProperty(text, "nodes");
        text = TextBlockParser.ReplaceArrayProperty(text, "links");
        return EncodeUtf16Bom(text);
    }

    #endregion

    #region EnvParticles

    /// <summary>
    /// 环境粒子补丁：替换 5 个关键标识符（让引擎找不到对应字段），
    /// 并把 rain_intensity 和 clouds_intensity 设为 0。
    /// 对应 Rust: env_particles(bytes)。
    /// </summary>
    private static byte[] EnvParticles(byte[] bytes)
    {
        var text = DecodeUtf16(bytes);
        text = text
            .Replace("\"area\"", "\"xrea\"")
            .Replace("\"fog\"", "\"xog\"")
            .Replace("\"screenspace_fog\"", "\"xcreenspace_fog\"")
            .Replace("\"effect_spawner\"", "\"xffect_spawner\"")
            .Replace("\"post_processing\"", "\"xost_processing\"");
        text = RainIntensityRe.Replace(text, "${1}0.0${3}");
        text = CloudsIntensityRe.Replace(text, "${1}0.0${3}");
        return EncodeUtf16Bom(text);
    }

    #endregion

    #region Delirium

    /// <summary>
    /// 谵妄补丁：根据原文件内容分三种情况重写为精简版本。
    /// 对应 Rust: delirium(bytes)。
    /// </summary>
    private static byte[] Delirium(byte[] bytes)
    {
        var text = DecodeUtf16(bytes);
        string outStr;
        if (text.Contains("Metadata/FmtParent") && !text.Contains("AnimatedRender"))
        {
            outStr = "version 3\nextends \"Metadata/FmtParent\"";
        }
        else if (text.Contains("Metadata/FmtParent") && text.Contains("AnimatedRender"))
        {
            outStr = "version 3\nextends \"Metadata/FmtParent\"\n\nclient\n{\n\tAnimatedRender\n\t{\n\t\tcannot_be_disabled = true\n\t}\n}";
        }
        else if (text.Contains("Metadata/Parent"))
        {
            outStr = "version 3\nextends \"Metadata/Parent\"\n\nBaseAnimationEvents\n{\n}\n\nAnimationController\n{\n\tmetadata = \"Art/Models/Effects/enviro_effects/weather_attachments/generic_rig/weather_rig.amd\"\n}\n\nclient\n{\n    ClientAnimationController\n    {\n        skeleton = \"Art/Models/Effects/enviro_effects/weather_attachments/generic_rig/weather_rig.ast\"\n    }\n\n    BoneGroups\n    {\n        bone_group = \"box false aux_box1 aux_box2 aux_box3 \"\n    }\n}";
        }
        else
        {
            outStr = text;
        }
        return EncodeUtf16Bom(outStr);
    }

    #endregion

    #region Particles

    /// <summary>
    /// 粒子补丁：将 .pet/.trl 文件内容清空为 "0"。
    /// 保护路径前缀下的文件保持不变（防止关键 BOSS 粒子缺失导致崩溃）。
    /// 对应 Rust: particles(path, bytes)。
    /// </summary>
    private static byte[] Particles(string path, byte[] bytes)
    {
        var normalized = PatchCatalog.NormalizePath(path);
        foreach (var prefix in PatchCatalog.ParticleProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }
        return EncodeUtf16Bom("0");
    }

    #endregion

    #region Effects

    /// <summary>
    /// 效果补丁：剥离 client 块中非必要的子块，只保留 5 个关键块。
    /// 启动场景保护路径与 effects 保护路径下的文件保持不变。
    /// 对应 Rust: effects(path, bytes)。
    /// </summary>
    private static byte[] Effects(string path, byte[] bytes)
    {
        var normalized = PatchCatalog.NormalizePath(path);
        if (PatchCatalog.IsStartupSceneProtected(path))
        {
            return bytes;
        }
        foreach (var prefix in PatchCatalog.EffectProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }

        foreach (var prefix in PatchCatalog.EffectNewProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }

        var text = DecodeUtf16(bytes);
        return EncodeUtf16Bom(TextBlockParser.StripClientBlocks(text, EffectKeepBlocks));
    }

    #endregion

    #region StripSounds

    /// <summary>
    /// 声音补丁（disable-sounds/skill-sounds/monster-sounds 共用）：
    /// 把 SoundEvents/SoundParams 块的体清空（{ ... } → {}），保留块名。
    /// 启动场景保护路径与 effects 保护路径下的文件保持不变。
    /// 对应 Rust: strip_sounds(path, bytes)。
    /// </summary>
    private static byte[] StripSounds(string path, byte[] bytes)
    {
        var normalized = PatchCatalog.NormalizePath(path);
        if (PatchCatalog.IsStartupSceneProtected(path))
        {
            return bytes;
        }
        foreach (var prefix in PatchCatalog.EffectProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }

        foreach (var prefix in PatchCatalog.EffectNewProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }

        var text = DecodeUtf16Lossless(bytes);
        if (text == null)
        {
            return bytes;
        }
        return EncodeUtf16Bom(TextBlockParser.EmptyNamedBlocks(text, SoundEmptyBlocks));
    }

    #endregion

    #region MtxSoft

    /// <summary>
    /// MTX 软补丁：清空微交易效果文件。
    /// - .epk → 空字符串（BOM + 无内容）
    /// - .pet/.trl → "0"
    /// - .ao/.aoc → 不修改
    /// 对应 Rust: mtx_soft(path, bytes)。
    /// </summary>
    private static byte[] MtxSoft(string path, byte[] bytes)
    {
        if (PatchCatalog.IsStartupSceneProtected(path))
        {
            return bytes;
        }
        if (PatchCatalog.IsMtxProtected(path))
        {
            return bytes;
        }
        if (PatchCatalog.EndsWithPathCi(path, ".epk"))
        {
            return EncodeUtf16Bom("");
        }
        if (PatchCatalog.EndsWithPathCi(path, ".pet") || PatchCatalog.EndsWithPathCi(path, ".trl"))
        {
            return EncodeUtf16Bom("0");
        }
        return bytes;
    }

    #endregion

    #region MonsterHpBar

    /// <summary>
    /// 始终显示怪物血条。
    /// 通过共享模板 metadata/monsters/monster.ot 给所有怪物 1 点基础护盾和生命，
    /// 使引擎在怪物受到伤害前就渲染 HP 条。
    /// 对应 Rust: monster_hp_bar(bytes)。
    /// </summary>
    private static byte[] MonsterHpBar(byte[] bytes)
    {
        var text = DecodeUtf16(bytes);
        if (text.Contains("base_maximum_energy_shield = 1", StringComparison.Ordinal)
            || text.Contains("base_maximum_life = 1", StringComparison.Ordinal))
        {
            return bytes;
        }

        var lines = text.Split(["\r\n", "\n"], StringSplitOptions.None).ToList();
        var insertAt = lines
            .FindIndex(line => line.Contains("item_drop_slots = 1", StringComparison.Ordinal));
        if (insertAt >= 0)
        {
            insertAt++;
        }
        else
        {
            insertAt = StatsBlockBodyStart(lines);
        }

        if (insertAt < 0 || insertAt > lines.Count)
        {
            return bytes;
        }

        lines.Insert(insertAt, "\tbase_maximum_life = 1");
        lines.Insert(insertAt, "\tbase_maximum_energy_shield = 1");
        return EncodeUtf16Bom(string.Join("\r\n", lines));
    }

    /// <summary>
    /// 查找 Stats { ... } 块体中第一个 `{` 之后的行索引。
    /// 对应 Rust: stats_block_body_start(lines)。
    /// </summary>
    private static int StatsBlockBodyStart(List<string> lines)
    {
        var stats = lines.FindIndex(line => line.Trim() == "Stats");
        if (stats < 0 || stats + 1 >= lines.Count)
        {
            return -1;
        }
        for (var i = stats + 1; i < lines.Count; i++)
        {
            if (lines[i].Trim() == "{")
            {
                return i + 1;
            }
        }
        return -1;
    }

    #endregion

    #region Effects_New
    //Effects_New
    private static byte[] Effects_New(string path, byte[] bytes)
    {
        if (PatchCatalog.IsStartupSceneProtected(path))
        {
            return bytes;
        }

        //受保护的不修改
        var normalized = PatchCatalog.NormalizePath(path);
        foreach (var prefix in PatchCatalog.EffectProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }

        foreach (var prefix in PatchCatalog.EffectNewProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }

        // .epk → 清空为 BOM（空字符串）。已经是 2 字节的跳过避免重复写入。
        if (PatchCatalog.EndsWithPathCi(path, ".epk"))
        {
            if (bytes.Length <= 2) return bytes;
            return EncodeUtf16Bom("");
        }

        // .ao → 清空 Lights + BaseAnimationEvents 块（场景灯光 + 动画事件）
        var text = DecodeUtf16Lossless(bytes);
        if (text == null)
        {
            return bytes;
        }
        var transformed = TextBlockParser.EmptyNamedBlocks(text, BlanketEmptyBlocks);
        // 只有在 EmptyNamedBlocks 实际修改了文本时才重新编码，
        // 避免对无目标块的 .ao 文件因 BOM 重编码产生无意义字节变更。
        if (string.Equals(text, transformed, StringComparison.Ordinal))
        {
            return bytes;
        }
        return EncodeUtf16Bom(transformed);
    }

    #endregion

    #region Blanket

    /// <summary>
    /// 地毯式补丁：覆盖整个 metadata/ 下的 .epk 和 .ao 文件。
    /// - .epk → 清空为 BOM（2 字节），杀掉所有 VFX 特效包
    /// - .ao → 清空 Lights + BaseAnimationEvents 块的体（保留块名）
    ///
    /// 参考第三方 TinyBundle 的修改策略：
    /// - 7,823 个 .epk 文件全部清空为 2 字节
    /// - 7,831 个 .ao 文件简化
    /// - 我们清空 Lights+BAE，覆盖 ~5,866 个 .ao（+8,304 .epk = ~14,170 文件）
    ///
    /// 保护启动场景路径（characterselection）以避免崩溃。
    /// </summary>
    private static byte[] Blanket(string path, byte[] bytes)
    {
        if (PatchCatalog.IsStartupSceneProtected(path))
        {
            return bytes;
        }

        //受保护的不修改
        var normalized = PatchCatalog.NormalizePath(path);
        foreach (var prefix in PatchCatalog.EffectProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }

        foreach (var prefix in PatchCatalog.EffectNewProtectedPrefixes)
        {
            if (normalized.StartsWith(prefix, StringComparison.Ordinal))
            {
                return bytes;
            }
        }

        // .epk → 清空为 BOM（空字符串）。已经是 2 字节的跳过避免重复写入。
        if (PatchCatalog.EndsWithPathCi(path, ".epk"))
        {
            if (bytes.Length <= 2) return bytes;
            return EncodeUtf16Bom("");
        }

        // .ao → 清空 Lights + BaseAnimationEvents 块（场景灯光 + 动画事件）
        var text = DecodeUtf16Lossless(bytes);
        if (text == null)
        {
            return bytes;
        }
        var transformed = TextBlockParser.EmptyNamedBlocks(text, BlanketEmptyBlocks);
        // 只有在 EmptyNamedBlocks 实际修改了文本时才重新编码，
        // 避免对无目标块的 .ao 文件因 BOM 重编码产生无意义字节变更。
        if (string.Equals(text, transformed, StringComparison.Ordinal))
        {
            return bytes;
        }
        return EncodeUtf16Bom(transformed);
    }

    #endregion


    #region TestTarget
    private static byte[] TestTarget(string path, byte[] bytes)
    {
        // .epk → 清空为 BOM（空字符串）。已经是 2 字节的跳过避免重复写入。
        if (PatchCatalog.EndsWithPathCi(path, ".epk"))
        {
            if (bytes.Length <= 2) return bytes;
            return EncodeUtf16Bom("");
        }

        // .ao → 清空 Lights + BaseAnimationEvents 块（场景灯光 + 动画事件）
        var text = DecodeUtf16Lossless(bytes);
        if (text == null)
        {
            return bytes;
        }
        var transformed = TextBlockParser.EmptyNamedBlocks(text, BlanketEmptyBlocks);
        // 只有在 EmptyNamedBlocks 实际修改了文本时才重新编码，
        // 避免对无目标块的 .ao 文件因 BOM 重编码产生无意义字节变更。
        if (string.Equals(text, transformed, StringComparison.Ordinal))
        {
            return bytes;
        }
        return EncodeUtf16Bom(transformed);
    }
    #endregion

    #region UTF-16 辅助

    /// <summary>
    /// 解码 UTF-16 LE 字节为字符串，去掉 BOM。
    /// 对应 Rust: decode_utf16(bytes)。
    /// </summary>
    private static string DecodeUtf16(byte[] bytes)
    {
        if (bytes.Length % 2 != 0)
        {
            throw new InvalidDataException($"UTF-16 文件字节长度为奇数：{bytes.Length}");
        }
        var text = Encoding.Unicode.GetString(bytes);
        // 去掉 BOM（如果有）
        if (text.Length > 0 && text[0] == '\uFEFF')
        {
            text = text[1..];
        }
        return text;
    }

    /// <summary>
    /// 尝试解码 UTF-16；解码失败时返回 null（而非抛异常）。
    /// 对应 Rust: decode_utf16_lossless(bytes)。
    /// </summary>
    private static string? DecodeUtf16Lossless(byte[] bytes)
    {
        if (bytes.Length % 2 != 0) return null;
        try
        {
            return DecodeUtf16(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 编码字符串为 UTF-16 LE 字节，带 BOM 前缀。
    /// 对应 Rust: encode_utf16_bom(text)。
    /// </summary>
    private static byte[] EncodeUtf16Bom(string text)
    {
        var outBytes = new byte[2 + text.Length * 2];
        outBytes[0] = 0xFF;
        outBytes[1] = 0xFE;
        Encoding.Unicode.GetBytes(text, 0, text.Length, outBytes, 2);
        return outBytes;
    }

    /// <summary>
    /// 解码 UTF-16 后做字符串替换，再编码回 UTF-16 with BOM。
    /// 对应 Rust: replace_utf16(bytes, replacements)。
    /// </summary>
    private static byte[] ReplaceUtf16(byte[] bytes, IReadOnlyList<(string From, string To)> replacements)
    {
        var text = DecodeUtf16(bytes);
        foreach (var (from, to) in replacements)
        {
            text = text.Replace(from, to);
        }
        return EncodeUtf16Bom(text);
    }

    /// <summary>
    /// 解码 UTF-16 后做正则替换，再编码回 UTF-16 with BOM。
    /// 对应 Rust: regex_utf16(bytes, regex, replacement)。
    /// </summary>
    private static byte[] RegexUtf16(byte[] bytes, Regex regex, string replacement)
    {
        var text = DecodeUtf16(bytes);
        return EncodeUtf16Bom(regex.Replace(text, replacement));
    }

    #endregion
}
