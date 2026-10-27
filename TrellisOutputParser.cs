// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/Season3D

namespace Season.ThreeD;

/// <summary>
/// Turns the worker's console lines into stage/step progress. The formats come from
/// trellis_cli.cpp / flow_runner.cpp:
/// <code>
/// [1/6] preprocess &lt;image&gt; (1024 cascade)
/// [2/6] DINOv3 conditioning
/// [3/6] sparse-structure flow + decode
/// [4/7] shape SLAT flow (LR 512 -&gt; upsample -&gt; HR 1024 cascade, max_tok=49152)
/// [5/7] FlexiDualGrid shape decode -&gt; mesh @res1024
/// [6/7] texture SLAT flow + PBR decode
/// [7/7] write &lt;out.glb&gt;
///       [flow] [########............]  6/12    12.3s  ~45s left
/// done in 812.4s -&gt; &lt;out.glb&gt; (+ &lt;out.ply&gt;)
/// </code>
/// The banners are numbered /6 for the first half and /7 for the second (upstream
/// inconsistency), so the enum mapping keys off the text, not the numbers.
/// </summary>
internal sealed class TrellisOutputParser
{
    private const string SeedMarker = "generating model with seed ";
    private const string FlowMarker = "[flow]";

    private TrellisStage _stage = TrellisStage.Idle;

    /// <summary>The seed the worker reported, or 0 when it has not printed one yet.</summary>
    public uint Seed { get; private set; }

    /// <summary>
    /// Parses one line. Returns true when the line carries progress worth reporting;
    /// stage banners, sampler steps and the final "done" line do, log noise does not.
    /// </summary>
    public bool TryParse(string line, out TrellisProgressUpdate update)
    {
        update = default;

        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();

        if (TryParseSeed(trimmed))
        {
            return false;
        }

        if (TryParseFlow(trimmed, out var step, out var total, out var elapsedSeconds, out var remainingSeconds))
        {
            // A flow line always belongs to the flow stage that last announced itself;
            // before any banner the sparse-structure sampler is the first one to run.
            var current = _stage is TrellisStage.Idle or TrellisStage.Completed ? TrellisStage.ShapeFlow : _stage;
            update = new TrellisProgressUpdate(
                current, trimmed, 0, 0, step, total, elapsedSeconds, remainingSeconds, false);
            return true;
        }

        if (trimmed.StartsWith("[bg-only] done", StringComparison.Ordinal) ||
            trimmed.StartsWith("done in ", StringComparison.Ordinal))
        {
            _stage = TrellisStage.Completed;
            update = new TrellisProgressUpdate(_stage, trimmed, 0, 0, 0, 0, null, null, true);
            return true;
        }

        if (TryParseStageBanner(trimmed, out var stageIndex, out var stageCount, out var label))
        {
            // An unknown banner still advances the message, but keeps the previous enum
            // so the UI does not invent a stage it cannot name.
            _stage = MapStage(label) ?? _stage;
            update = new TrellisProgressUpdate(
                _stage, trimmed, stageIndex, stageCount, 0, 0, null, null, false);
            return true;
        }

        return false;
    }

    private bool TryParseSeed(string line)
    {
        var index = line.IndexOf(SeedMarker, StringComparison.Ordinal);
        if (index < 0)
        {
            return false;
        }

        var rest = line[(index + SeedMarker.Length)..];
        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
        {
            digits++;
        }

        if (digits > 0 &&
            uint.TryParse(rest[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out var seed))
        {
            Seed = seed;
        }

        // The announcement is a log detail, not a stage transition.
        return true;
    }

    private static bool TryParseFlow(
        string line,
        out int step,
        out int total,
        out double? elapsedSeconds,
        out double? remainingSeconds)
    {
        step = 0;
        total = 0;
        elapsedSeconds = null;
        remainingSeconds = null;

        var flowIndex = line.IndexOf(FlowMarker, StringComparison.Ordinal);
        if (flowIndex < 0)
        {
            return false;
        }

        // The bar sits in its own brackets after the marker: [flow] [####....] 3/12 ...
        var barOpen = line.IndexOf('[', flowIndex + FlowMarker.Length);
        if (barOpen < 0)
        {
            return false;
        }

        var barClose = line.IndexOf(']', barOpen + 1);
        if (barClose < 0)
        {
            return false;
        }

        var tokens = line[(barClose + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0)
        {
            return false;
        }

        var fraction = tokens[0].Split('/');
        if (fraction.Length != 2 ||
            !int.TryParse(fraction[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out step) ||
            !int.TryParse(fraction[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out total))
        {
            return false;
        }

        // "  12.3s  ~45s left" (or "starting" before the first step has a rate).
        for (var i = 1; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Length < 2 || token[^1] != 's')
            {
                continue;
            }

            if (!double.TryParse(token.Trim('~', 's'), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                continue;
            }

            if (token[0] == '~')
            {
                remainingSeconds = seconds;
            }
            else
            {
                elapsedSeconds = seconds;
            }
        }

        return true;
    }

    private static bool TryParseStageBanner(
        string line,
        out int stageIndex,
        out int stageCount,
        out string label)
    {
        stageIndex = 0;
        stageCount = 0;
        label = string.Empty;

        if (line.Length < 5 || line[0] != '[')
        {
            return false;
        }

        var close = line.IndexOf(']', 1);
        if (close < 0)
        {
            return false;
        }

        var numbers = line[1..close].Split('/');
        if (numbers.Length != 2 ||
            !int.TryParse(numbers[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out stageIndex) ||
            !int.TryParse(numbers[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out stageCount))
        {
            return false;
        }

        label = line[(close + 1)..].Trim();
        return true;
    }

    private static TrellisStage? MapStage(string label)
    {
        if (label.Contains("preprocess", StringComparison.OrdinalIgnoreCase))
        {
            return TrellisStage.Preprocess;
        }

        if (label.Contains("DINOv3", StringComparison.OrdinalIgnoreCase))
        {
            return TrellisStage.Conditioning;
        }

        if (label.Contains("sparse-structure", StringComparison.OrdinalIgnoreCase))
        {
            return TrellisStage.SparseStructure;
        }

        if (label.Contains("shape SLAT", StringComparison.OrdinalIgnoreCase))
        {
            return TrellisStage.ShapeFlow;
        }

        if (label.Contains("shape decode", StringComparison.OrdinalIgnoreCase))
        {
            return TrellisStage.ShapeDecode;
        }

        if (label.Contains("texture SLAT", StringComparison.OrdinalIgnoreCase))
        {
            return TrellisStage.Texture;
        }

        if (label.Contains("cleaned dump", StringComparison.OrdinalIgnoreCase) ||
            label.StartsWith("write ", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("QEM simplify", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("remesh postproc", StringComparison.OrdinalIgnoreCase) ||
            label.Contains("textured GLB", StringComparison.OrdinalIgnoreCase))
        {
            return TrellisStage.Postprocess;
        }

        return null;
    }
}
