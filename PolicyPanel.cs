using Godot;
using System;
using System.Collections.Generic;
using System.Linq;

// ── PolicyPanel ────────────────────────────────────────────────────────

/// <summary>
/// The Panderer's Code: the campaign's one irreversible decision, and the
/// ladder that follows from it.
///
/// The panel has two states. Before a path is chosen it shows both tier-0
/// acts side by side and refuses to let a single click end the question — a
/// choice arms first, states plainly that it closes the other branch for the
/// rest of the run, and only then enacts. After a path is chosen it shows
/// that branch's four tiers as a ladder and the abandoned branch greyed and
/// marked closed.
///
/// Everything it reports is read off <see cref="PolicyTreeManager"/> rather
/// than recomputed here: statuses come from
/// <see cref="PolicyTreeManager.GetAllPolicyStatuses"/>, refusals come from
/// the <see cref="EnactmentResult"/> the manager hands back, and the footer
/// prints <see cref="PolicyTreeManager.GetModifierSummary"/> verbatim. When
/// the manager and the panel disagree, the manager wins and the panel says so.
///
/// Built entirely in code. Safe with no manager bound, no branch chosen, a
/// branch chosen, all four tiers enacted, and an active cooldown.
/// </summary>
public partial class PolicyPanel : Control
{
    // ── Signals ────────────────────────────────────────────────────────

    /// <summary>A policy was signed. Carries the manager's policy key, e.g. "WF1".</summary>
    [Signal]
    public delegate void OnPolicyEnactedEventHandler(string policyKey);

    /// <summary>The player dismissed the panel.</summary>
    [Signal]
    public delegate void OnCloseRequestedEventHandler();

    // ── Tuning ─────────────────────────────────────────────────────────

    /// <summary>Panel width. Wide enough for a tier's effect block without wrapping to shreds.</summary>
    public const int PanelWidth = 400;

    // ── State ──────────────────────────────────────────────────────────

    private PolicyTreeManager _policies;

    /// <summary>
    /// The tier-0 key the player has armed but not yet confirmed. Null means
    /// nothing is armed, which is the only state a stray click can leave.
    /// </summary>
    private string _armedKey;

    private string _statusText = "";
    private Color _statusColour = IsoTheme.TextMuted;

    // ── Widgets ────────────────────────────────────────────────────────

    private VBoxContainer _body;
    private Label _subtitle;
    private Label _status;
    private Label _modifiers;
    private Label _clock;
    private bool _built;

    // ── Lifecycle ──────────────────────────────────────────────────────

    public override void _Ready() => Build();

    /// <summary>Point the panel at a policy tree. Null is legal — the panel simply says so.</summary>
    public void Bind(PolicyTreeManager policies)
    {
        _policies = policies;
        _armedKey = null;

        Build();
        Refresh();
    }

    private void Build()
    {
        if (_built) return;
        _built = true;

        CustomMinimumSize = new Vector2(PanelWidth, 0);

        // SetAnchorsPreset alone rewrites the anchors and leaves the offsets
        // stale, so the PanelContainer inside collapses to its minimum size —
        // the bug that ate an afternoon on InfluencePanel. The AndOffsets
        // variant zeroes both, and has to run after AddChild because anchors
        // resolve against the parent.
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var frame = new PanelContainer { Name = "Frame" };
        frame.AddThemeStyleboxOverride("panel", HudStyle.FramedPanel());
        AddChild(frame);

        frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);

        var outer = new VBoxContainer();
        outer.AddThemeConstantOverride("separation", 8);
        frame.AddChild(outer);

        // ── Header ─────────────────────────────────────────────────────

        var header = new HBoxContainer();
        outer.AddChild(header);

        var titles = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        header.AddChild(titles);

        titles.AddChild(HudStyle.MakeLabel("THE PANDERER'S CODE", 16, IsoTheme.Gold));
        _subtitle = HudStyle.MakeLabel("No path chosen", 11, IsoTheme.TextMuted);
        titles.AddChild(_subtitle);

        var close = new Button { Text = "✕", CustomMinimumSize = new Vector2(30, 30) };
        HudStyle.StyleButton(close, IsoTheme.Danger);
        close.Pressed += () =>
        {
            _armedKey = null;
            EmitSignal(SignalName.OnCloseRequested);
        };
        header.AddChild(close);

        // ── Body ───────────────────────────────────────────────────────

        var scroll = new ScrollContainer
        {
            SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        outer.AddChild(scroll);

        _body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _body.AddThemeConstantOverride("separation", 10);
        scroll.AddChild(_body);

        // ── Footer ─────────────────────────────────────────────────────

        var footer = new PanelContainer();
        footer.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, IsoTheme.GoldDim, radius: 8, borderWidth: 1, padding: 8));
        outer.AddChild(footer);

        var footerBox = new VBoxContainer();
        footerBox.AddThemeConstantOverride("separation", 3);
        footer.AddChild(footerBox);

        footerBox.AddChild(HudStyle.MakeLabel("STANDING EFFECTS", 9, IsoTheme.Gold));

        _modifiers = Wrap("(no active modifiers)", IsoTheme.TextPrimary);
        footerBox.AddChild(_modifiers);

        _clock = Wrap("", IsoTheme.TextMuted);
        footerBox.AddChild(_clock);

        _status = Wrap("", IsoTheme.TextMuted);
        footerBox.AddChild(_status);
    }

    // ── Rendering ──────────────────────────────────────────────────────

    /// <summary>Rebuild the whole body from the manager's current state.</summary>
    public void Refresh()
    {
        Build();
        if (_body == null) return;

        foreach (var child in _body.GetChildren()) child.QueueFree();

        if (_policies == null)
        {
            _subtitle.Text = "No code in force";
            _body.AddChild(Wrap(
                "There is no policy tree bound to this panel.", IsoTheme.TextMuted));
            RenderFooter();
            return;
        }

        var tree = ResolveTree();
        var statuses = GetStatuses();
        var branch = _policies.ActiveBranch;

        _subtitle.Text = branch == PolicyBranch.None
            ? "No path chosen"
            : BranchName(branch);

        if (branch == PolicyBranch.None)
            RenderChoice(tree, statuses);
        else
            RenderLadder(tree, statuses, branch);

        RenderFooter();
    }

    // ── The unmade choice ──────────────────────────────────────────────

    private void RenderChoice(
        IReadOnlyDictionary<string, PolicyDefinition> tree,
        IReadOnlyDictionary<string, PolicyStatus> statuses)
    {
        _body.AddChild(Wrap(
            "Two ways to run a house. Signing either one closes the other for " +
            "the rest of this run — there is no repeal, and no second act.",
            IsoTheme.TextPrimary));

        foreach (var branch in new[] { PolicyBranch.WorkforceProtection, PolicyBranch.SystemicExploitation })
        {
            var entry = FindTierZero(tree, branch);
            if (entry.Key == null) continue;

            _body.AddChild(BuildBranchOffer(entry.Key, entry.Value, StatusOf(statuses, entry.Key), branch));
        }
    }

    private Control BuildBranchOffer(
        string key, PolicyDefinition policy, PolicyStatus status, PolicyBranch branch)
    {
        var accent = BranchColour(branch);

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, accent, radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        box.AddChild(HudStyle.MakeLabel(BranchName(branch).ToUpperInvariant(), 11, accent));
        box.AddChild(HudStyle.MakeLabel(policy.PolicyName, 13, IsoTheme.TextPrimary));

        box.AddChild(Wrap(BranchPitch(branch), IsoTheme.TextMuted));

        if (!string.IsNullOrWhiteSpace(policy.Description))
            box.AddChild(Wrap(policy.Description, IsoTheme.TextMuted));

        AppendEffects(box, policy, accent);

        box.AddChild(Wrap(
            $"Signing this closes {BranchName(Opposite(branch))} permanently.",
            IsoTheme.Warning));

        AppendTierZeroControls(box, key, policy, status, accent, branch);

        return card;
    }

    /// <summary>
    /// The two-step gate. A first press only arms the choice and swaps in an
    /// explicit confirmation, so no single click can end the campaign's one
    /// irreversible decision.
    /// </summary>
    private void AppendTierZeroControls(
        VBoxContainer box, string key, PolicyDefinition policy,
        PolicyStatus status, Color accent, PolicyBranch branch)
    {
        if (status == PolicyStatus.Enacted)
        {
            box.AddChild(Wrap("SIGNED", 11, accent));
            return;
        }

        if (status != PolicyStatus.Available)
        {
            box.AddChild(Wrap(LockReason(policy), IsoTheme.Warning));
            return;
        }

        if (!_policies.CanEnactNow)
        {
            box.AddChild(Wrap(
                $"The ink is still wet. {_policies.CooldownRemaining} day(s) before anything else is signed.",
                IsoTheme.Warning));
            return;
        }

        if (_armedKey != key)
        {
            var arm = new Button
            {
                Text = "Take this path",
                SizeFlagsHorizontal = SizeFlags.ExpandFill
            };
            HudStyle.StyleButton(arm, accent, fontSize: 12);
            arm.Pressed += () =>
            {
                _armedKey = key;
                SetStatus("", IsoTheme.TextMuted);
                Refresh();
            };
            box.AddChild(arm);
            return;
        }

        // Armed. Say the consequence in full, then require a second, named press.
        box.AddChild(Wrap(
            $"This is permanent. {BranchName(Opposite(branch))} will be closed " +
            "for the rest of the run, and this cannot be undone by any later policy, " +
            "any amount of money, or any change of mind.",
            IsoTheme.Danger));

        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        box.AddChild(row);

        var cancel = new Button { Text = "Not yet", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        HudStyle.StyleButton(cancel, IsoTheme.GoldDim, fontSize: 12);
        cancel.Pressed += () =>
        {
            _armedKey = null;
            Refresh();
        };
        row.AddChild(cancel);

        var confirm = new Button
        {
            Text = "Sign it — permanent",
            SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        HudStyle.StyleButton(confirm, IsoTheme.Danger, fontSize: 12);
        confirm.Pressed += () => Enact(key);
        row.AddChild(confirm);
    }

    // ── The chosen ladder ──────────────────────────────────────────────

    private void RenderLadder(
        IReadOnlyDictionary<string, PolicyDefinition> tree,
        IReadOnlyDictionary<string, PolicyStatus> statuses,
        PolicyBranch branch)
    {
        var accent = BranchColour(branch);

        _body.AddChild(Wrap(
            $"{BranchName(branch)} is in force. {BranchPitch(branch)}", accent));

        var ladder = tree
            .Where(kvp => kvp.Value != null && kvp.Value.Branch == branch)
            .OrderBy(kvp => kvp.Value.Tier)
            .ToList();

        foreach (var kvp in ladder)
            _body.AddChild(BuildPolicyCard(kvp.Key, kvp.Value, StatusOf(statuses, kvp.Key), accent));

        _body.AddChild(BuildClosedBranchCard(tree, Opposite(branch)));
    }

    private Control BuildPolicyCard(
        string key, PolicyDefinition policy, PolicyStatus status, Color accent)
    {
        var trim = status switch
        {
            PolicyStatus.Enacted => accent,
            PolicyStatus.Available => IsoTheme.Gold,
            _ => IsoTheme.GoldDim.Darkened(0.3f)
        };

        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(HudStyle.RowFill, trim, radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 4);
        card.AddChild(box);

        var head = new HBoxContainer();
        head.AddThemeConstantOverride("separation", 6);
        box.AddChild(head);

        var tier = HudStyle.MakeLabel($"TIER {policy.Tier}", 9, IsoTheme.TextMuted);
        tier.CustomMinimumSize = new Vector2(52, 0);
        head.AddChild(tier);

        var name = HudStyle.MakeLabel(policy.PolicyName, 13,
            status == PolicyStatus.Locked ? IsoTheme.TextMuted : IsoTheme.TextPrimary);
        name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        name.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        head.AddChild(name);

        head.AddChild(HudStyle.MakeLabel(StatusWord(status), 9, StatusColour(status, accent)));

        if (!string.IsNullOrWhiteSpace(policy.Description))
            box.AddChild(Wrap(policy.Description, IsoTheme.TextMuted));

        AppendEffects(box, policy, status == PolicyStatus.Locked ? IsoTheme.TextMuted : accent);

        switch (status)
        {
            case PolicyStatus.Enacted:
                box.AddChild(Wrap("Signed. In force for the rest of the run.", accent));
                break;

            case PolicyStatus.Locked:
                box.AddChild(Wrap(LockReason(policy), IsoTheme.Warning));
                break;

            case PolicyStatus.Available when !_policies.CanEnactNow:
                box.AddChild(Wrap(
                    $"Cooldown: {_policies.CooldownRemaining} day(s) before the next signing.",
                    IsoTheme.Warning));
                AppendEnactButton(box, key, accent, disabled: true);
                break;

            case PolicyStatus.Available:
                AppendEnactButton(box, key, accent, disabled: false);
                break;
        }

        return card;
    }

    private void AppendEnactButton(VBoxContainer box, string key, Color accent, bool disabled)
    {
        var button = new Button
        {
            Text = "Enact",
            SizeFlagsHorizontal = SizeFlags.ExpandFill,
            Disabled = disabled || key == null
        };
        HudStyle.StyleButton(button, accent, fontSize: 12);

        if (!button.Disabled)
            button.Pressed += () => Enact(key);

        box.AddChild(button);
    }

    private Control BuildClosedBranchCard(
        IReadOnlyDictionary<string, PolicyDefinition> tree, PolicyBranch closed)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel",
            HudStyle.Box(IsoTheme.FacadeShadow.Darkened(0.2f), IsoTheme.GoldDim.Darkened(0.5f),
                radius: 8, borderWidth: 1, padding: 8));

        var box = new VBoxContainer();
        box.AddThemeConstantOverride("separation", 3);
        card.AddChild(box);

        box.AddChild(HudStyle.MakeLabel(
            $"{BranchName(closed).ToUpperInvariant()} — CLOSED FOR THIS RUN", 10,
            IsoTheme.TextMuted));

        var names = tree
            .Where(kvp => kvp.Value != null && kvp.Value.Branch == closed)
            .OrderBy(kvp => kvp.Value.Tier)
            .Select(kvp => $"T{kvp.Value.Tier} {kvp.Value.PolicyName}")
            .ToList();

        box.AddChild(Wrap(
            names.Count > 0
                ? string.Join("  ·  ", names)
                : "(no policies defined)",
            IsoTheme.TextMuted.Darkened(0.35f)));

        box.AddChild(Wrap(
            "Sealed the day the other act was signed. Nothing here can be reached again.",
            IsoTheme.TextMuted.Darkened(0.2f)));

        return card;
    }

    // ── Footer ─────────────────────────────────────────────────────────

    private void RenderFooter()
    {
        if (_modifiers == null) return;

        if (_policies == null)
        {
            _modifiers.Text = "(no policy tree)";
            _clock.Text = $"Day {CurrentDay()}";
        }
        else
        {
            string summary;
            try { summary = _policies.GetModifierSummary(); }
            catch (Exception) { summary = "(unavailable)"; }

            _modifiers.Text = string.IsNullOrWhiteSpace(summary) ? "(no active modifiers)" : summary;

            var remaining = _policies.CooldownRemaining;
            _clock.Text = remaining > 0
                ? $"Day {CurrentDay()}  ·  next signing in {remaining} day(s)"
                : $"Day {CurrentDay()}  ·  ready to sign";
        }

        _status.Text = _statusText ?? "";
        _status.AddThemeColorOverride("font_color", _statusColour);
        _status.Visible = !string.IsNullOrEmpty(_status.Text);
    }

    private void SetStatus(string text, Color colour)
    {
        _statusText = text ?? "";
        _statusColour = colour;

        if (_status == null) return;
        _status.Text = _statusText;
        _status.AddThemeColorOverride("font_color", _statusColour);
        _status.Visible = !string.IsNullOrEmpty(_statusText);
    }

    // ── Enactment ──────────────────────────────────────────────────────

    private void Enact(string key)
    {
        if (_policies == null || string.IsNullOrEmpty(key))
        {
            SetStatus("Nothing to sign.", IsoTheme.Warning);
            return;
        }

        _armedKey = null;

        EnactmentResult result;
        try
        {
            result = _policies.EnactPolicy(key);
        }
        catch (Exception e)
        {
            SetStatus($"Enactment failed: {e.Message}", IsoTheme.Danger);
            Refresh();
            return;
        }

        Refresh();

        var message = string.IsNullOrWhiteSpace(result.Message)
            ? (result.Success ? "Signed." : "Refused, without a reason given.")
            : result.Message;

        SetStatus(message, result.Success ? IsoTheme.Good : IsoTheme.Danger);

        if (result.Success)
            EmitSignal(SignalName.OnPolicyEnacted, key);
    }

    // ── Reading the manager ────────────────────────────────────────────

    /// <summary>
    /// Pair the manager's policy keys — the only handle
    /// <see cref="PolicyTreeManager.EnactPolicy"/> accepts — with the
    /// definitions it exposes per branch. Keys are grouped by their branch
    /// prefix and zipped against that branch's tier-ordered definitions.
    /// Anything that does not line up is simply left out rather than guessed at.
    /// </summary>
    private Dictionary<string, PolicyDefinition> ResolveTree()
    {
        var map = new Dictionary<string, PolicyDefinition>();
        if (_policies == null) return map;

        var statuses = GetStatuses();
        if (statuses.Count == 0) return map;

        foreach (var branch in new[] { PolicyBranch.WorkforceProtection, PolicyBranch.SystemicExploitation })
        {
            List<PolicyDefinition> defs;
            try { defs = _policies.GetPoliciesInBranch(branch) ?? new List<PolicyDefinition>(); }
            catch (Exception) { continue; }

            var prefix = BranchPrefix(branch);
            var keys = statuses.Keys
                .Where(k => !string.IsNullOrEmpty(k) &&
                            k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k, StringComparer.Ordinal)
                .ToList();

            for (int i = 0; i < Math.Min(keys.Count, defs.Count); i++)
            {
                if (defs[i] != null) map[keys[i]] = defs[i];
            }
        }

        return map;
    }

    private Dictionary<string, PolicyStatus> GetStatuses()
    {
        if (_policies == null) return new Dictionary<string, PolicyStatus>();

        try
        {
            return _policies.GetAllPolicyStatuses() ?? new Dictionary<string, PolicyStatus>();
        }
        catch (Exception)
        {
            return new Dictionary<string, PolicyStatus>();
        }
    }

    private static PolicyStatus StatusOf(
        IReadOnlyDictionary<string, PolicyStatus> statuses, string key) =>
        key != null && statuses.TryGetValue(key, out var s) ? s : PolicyStatus.Locked;

    private static KeyValuePair<string, PolicyDefinition> FindTierZero(
        IReadOnlyDictionary<string, PolicyDefinition> tree, PolicyBranch branch) =>
        tree.FirstOrDefault(kvp =>
            kvp.Value != null && kvp.Value.Branch == branch && kvp.Value.Tier == 0);

    /// <summary>
    /// Why a policy is not signable, mirroring the manager's own checks in the
    /// order it applies them, and reading its state rather than a local copy.
    /// </summary>
    private string LockReason(PolicyDefinition policy)
    {
        if (policy == null) return "Unavailable.";
        if (_policies == null) return "No policy tree bound.";

        var day = CurrentDay();
        var branch = _policies.ActiveBranch;

        if (policy.Tier == 0 && branch != PolicyBranch.None)
            return $"A path is already set: {BranchName(branch)}. This act can no longer be signed.";

        if (policy.Tier > 0 && branch != policy.Branch)
            return branch == PolicyBranch.None
                ? $"Requires the {BranchName(policy.Branch)} act to be signed first."
                : $"Belongs to {BranchName(policy.Branch)}, which is closed for this run.";

        if (!string.IsNullOrEmpty(policy.PrerequisitePolicy) &&
            !_policies.EnactedPolicies.Contains(policy.PrerequisitePolicy))
            return $"Prerequisite not met: {policy.PrerequisitePolicy} must be signed first.";

        if (day < policy.MinDay)
            return $"Not before day {policy.MinDay}. It is day {day}.";

        if (!_policies.CanEnactNow)
            return $"Cooldown: {_policies.CooldownRemaining} day(s) before the next signing.";

        return "Unavailable.";
    }

    private int CurrentDay() => GameStateManager.Instance?.DayCount ?? 0;

    // ── Copy ───────────────────────────────────────────────────────────

    private static string BranchName(PolicyBranch branch) => branch switch
    {
        PolicyBranch.WorkforceProtection => "Workforce Protection",
        PolicyBranch.SystemicExploitation => "Systemic Exploitation",
        _ => "No path chosen"
    };

    private static string BranchPrefix(PolicyBranch branch) =>
        branch == PolicyBranch.WorkforceProtection ? "WF" : "SE";

    private static PolicyBranch Opposite(PolicyBranch branch) => branch switch
    {
        PolicyBranch.WorkforceProtection => PolicyBranch.SystemicExploitation,
        PolicyBranch.SystemicExploitation => PolicyBranch.WorkforceProtection,
        _ => PolicyBranch.None
    };

    private static Color BranchColour(PolicyBranch branch) => branch switch
    {
        PolicyBranch.WorkforceProtection => IsoTheme.Good,
        PolicyBranch.SystemicExploitation => IsoTheme.Danger,
        _ => IsoTheme.Gold
    };

    private static string BranchPitch(PolicyBranch branch) => branch switch
    {
        PolicyBranch.WorkforceProtection =>
            "Doctors, a cut of the profit, and men on the door. It costs money every " +
            "day the house is open. Staff stay, they recover faster, and the place " +
            "holds together when someone comes looking.",

        PolicyBranch.SystemicExploitation =>
            "Longer shifts, loans the staff cannot work off, and a file on everyone " +
            "who walks in. It is cheaper and it earns more. The staff cannot leave, " +
            "and the city notices.",

        _ => ""
    };

    private static string StatusWord(PolicyStatus status) => status switch
    {
        PolicyStatus.Enacted => "ENACTED",
        PolicyStatus.Available => "AVAILABLE",
        _ => "LOCKED"
    };

    private static Color StatusColour(PolicyStatus status, Color accent) => status switch
    {
        PolicyStatus.Enacted => accent,
        PolicyStatus.Available => IsoTheme.Gold,
        _ => IsoTheme.TextMuted
    };

    // ── Small builders ─────────────────────────────────────────────────

    private static void AppendEffects(VBoxContainer box, PolicyDefinition policy, Color accent)
    {
        if (policy == null || string.IsNullOrWhiteSpace(policy.EffectsDescription)) return;

        box.AddChild(HudStyle.MakeLabel("EFFECTS", 9, IsoTheme.Gold));

        foreach (var line in policy.EffectsDescription.Split('\n'))
        {
            var text = line.Trim();
            if (text.Length == 0) continue;
            box.AddChild(Wrap($"· {text}", accent));
        }
    }

    private static Label Wrap(string text, Color colour) => Wrap(text, 10, colour);

    private static Label Wrap(string text, int fontSize, Color colour)
    {
        var label = HudStyle.MakeLabel(text, fontSize, colour);
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        return label;
    }

    public override string ToString() =>
        $"[PolicyPanel] {(_policies == null ? "unbound" : _policies.ActiveBranch.ToString())} " +
        $"armed={_armedKey ?? "none"}";
}
