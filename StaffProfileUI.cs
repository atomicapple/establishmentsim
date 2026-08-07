using Godot;
using System;
using System.Collections.Generic;

/// <summary>
/// Staff micro-management profile screen. Renders individual employee
/// management cards with stat meters, specialization matrix, and
/// action buttons (Assign Shift, Train Skill, Pay Bonus, Terminate).
/// Button states refresh immediately on click via signal feedback.
/// </summary>
public partial class StaffProfileUI : Control
{
    [Signal] public delegate void OnActionExecutedEventHandler(string staffName, string action);

    private StaffMember _selectedStaff;
    private VBoxContainer _cardContainer;
    private readonly Dictionary<string, Control> _staffCards = new();
    private readonly List<StaffMember> _allStaff = new();

    private Label _nameLabel, _roleLabel;
    private ProgressBar _charismaBar, _negotiationBar, _discretionBar;
    private ProgressBar _stressBar, _satisfactionBar, _traumaBar;
    private Label _charismaLabel, _negotiationLabel, _discretionLabel;
    private Label _stressLabel, _satisfactionLabel, _traumaLabel;
    private Button _btnAssign, _btnTrain, _btnBonus, _btnTerminate;
    private OptionButton _specializationDropdown;
    private Label _trainSelectLabel;
    private OptionButton _trainStatDropdown;
    private LineEdit _bonusInput;

    private static readonly Color BarGreen = new(0.2f, 0.8f, 0.3f);
    private static readonly Color BarYellow = new(0.9f, 0.7f, 0.2f);
    private static readonly Color BarRed = new(0.9f, 0.2f, 0.2f);
    private static readonly Color BarBlue = new(0.2f, 0.5f, 0.9f);

    public StaffMember SelectedStaff => _selectedStaff;

    public override void _Ready()
    {
        BuildUI();
        GD.Print("[StaffProfileUI] Initialized.");
    }

    public void LoadStaff(StaffMember staff)
    {
        _selectedStaff = staff;
        RefreshDisplay();
    }

    public void RegisterStaffMember(StaffMember staff)
    {
        if (!_allStaff.Contains(staff))
            _allStaff.Add(staff);
    }

    // ── UI Construction ────────────────────────────────────────────────

    private void BuildUI()
    {
        SetAnchorsPreset(Control.LayoutPreset.FullRect);

        var scroll = new ScrollContainer();
        scroll.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        AddChild(scroll);

        _cardContainer = new VBoxContainer();
        _cardContainer.AddThemeConstantOverride("separation", 16);
        scroll.AddChild(_cardContainer);

        // ── Identity Section ──
        _nameLabel = MakeLabel("Select a staff member", 20, Colors.White);
        _cardContainer.AddChild(_nameLabel);
        _roleLabel = MakeLabel("", 14, new Color(0.7f, 0.7f, 0.8f));
        _cardContainer.AddChild(_roleLabel);
        _cardContainer.AddChild(new HSeparator());

        // ── RPG Stats ──
        _cardContainer.AddChild(MakeSectionHeader("RPG Stats"));
        (_charismaBar, _charismaLabel) = MakeStatRow("Charisma", BarBlue);
        (_negotiationBar, _negotiationLabel) = MakeStatRow("Negotiation", BarBlue);
        (_discretionBar, _discretionLabel) = MakeStatRow("Discretion", BarBlue);

        _cardContainer.AddChild(new HSeparator());

        // ── Agency Variables ──
        _cardContainer.AddChild(MakeSectionHeader("Agency Variables"));
        (_stressBar, _stressLabel) = MakeStatRow("Stress", BarRed);
        (_satisfactionBar, _satisfactionLabel) = MakeStatRow("Satisfaction", BarGreen);
        (_traumaBar, _traumaLabel) = MakeStatRow("Trauma", BarYellow);

        _cardContainer.AddChild(new HSeparator());

        // ── Specialization Matrix ──
        _cardContainer.AddChild(MakeSectionHeader("Specialization"));
        _specializationDropdown = new OptionButton();
        _specializationDropdown.AddItem("High-Society (Charisma focus)");
        _specializationDropdown.AddItem("Therapeutic (Trauma recovery)");
        _specializationDropdown.AddItem("Information Gathering (Discretion focus)");
        _cardContainer.AddChild(_specializationDropdown);

        _cardContainer.AddChild(new HSeparator());

        // ── Action Buttons ──
        _cardContainer.AddChild(MakeSectionHeader("Actions"));

        var btnRow1 = new HBoxContainer();
        btnRow1.AddThemeConstantOverride("separation", 8);

        _btnAssign = MakeActionButton("Assign Shift", new Color(0.2f, 0.5f, 0.8f));
        _btnAssign.Pressed += () => ExecuteAction("assign_shift");
        btnRow1.AddChild(_btnAssign);

        _trainSelectLabel = MakeLabel("Skill:", 12, new Color(0.7f, 0.7f, 0.7f));
        btnRow1.AddChild(_trainSelectLabel);
        _trainStatDropdown = new OptionButton();
        _trainStatDropdown.AddItem("Charisma");
        _trainStatDropdown.AddItem("Negotiation");
        _trainStatDropdown.AddItem("Discretion");
        btnRow1.AddChild(_trainStatDropdown);

        _btnTrain = MakeActionButton("Train", new Color(0.2f, 0.7f, 0.4f));
        _btnTrain.Pressed += () => ExecuteAction("train_skill");
        btnRow1.AddChild(_btnTrain);
        _cardContainer.AddChild(btnRow1);

        var btnRow2 = new HBoxContainer();
        btnRow2.AddThemeConstantOverride("separation", 8);

        _bonusInput = new LineEdit();
        _bonusInput.PlaceholderText = "Bonus amount $";
        _bonusInput.CustomMinimumSize = new Vector2(100, 0);
        btnRow2.AddChild(_bonusInput);

        _btnBonus = MakeActionButton("Pay Bonus", new Color(0.2f, 0.7f, 0.2f));
        _btnBonus.Pressed += () => ExecuteAction("pay_bonus");
        btnRow2.AddChild(_btnBonus);

        _btnTerminate = MakeActionButton("Terminate Contract", new Color(0.8f, 0.2f, 0.2f));
        _btnTerminate.Pressed += () => ExecuteAction("terminate");
        btnRow2.AddChild(_btnTerminate);
        _cardContainer.AddChild(btnRow2);
    }

    // ── Actions ────────────────────────────────────────────────────────

    private void ExecuteAction(string action)
    {
        if (_selectedStaff == null) return;

        switch (action)
        {
            case "assign_shift":
                var specializations = new[] { "HighSociety", "Therapeutic", "InformationGathering" };
                var spec = specializations[_specializationDropdown.Selected];
                _selectedStaff.ApplyShiftWorkload(0.5f, _selectedStaff.Discretion);
                GD.Print($"[StaffProfileUI] {_selectedStaff.StaffName} assigned shift ({spec}).");
                break;

            case "train_skill":
                var stats = new[] { "Charisma", "Negotiation", "Discretion" };
                _selectedStaff.Train(stats[_trainStatDropdown.Selected], 4f);
                break;

            case "pay_bonus":
                if (double.TryParse(_bonusInput.Text, out double amount) && amount > 0)
                {
                    if (GameStateManager.Instance != null && GameStateManager.Instance.Cash >= amount)
                    {
                        _selectedStaff.ReceiveBonus(amount);
                        GameStateManager.Instance.Cash -= amount;
                    }
                }
                _bonusInput.Text = "";
                break;

            case "terminate":
                // Previously this only zeroed the agency variables and printed —
                // the staff member stayed on the books forever. Removal is the
                // roster's job, and it refuses if a contract debt binds them.
                var terminatedName = _selectedStaff.StaffName;
                var removed = StaffRoster.Instance?.Remove(
                    _selectedStaff.Id, "terminated by management") ?? false;

                if (removed)
                {
                    EmitSignal(SignalName.OnActionExecuted, terminatedName, action);
                    _selectedStaff = null;
                    return;
                }

                GD.Print($"[StaffProfileUI] Could not terminate {terminatedName} — " +
                         $"contract-bound or not on the roster.");
                break;
        }

        RefreshDisplay();
        EmitSignal(SignalName.OnActionExecuted, _selectedStaff.StaffName, action);
    }

    private void RefreshDisplay()
    {
        if (_selectedStaff == null) return;

        _nameLabel.Text = _selectedStaff.StaffName;
        _roleLabel.Text = _selectedStaff.Role;

        _charismaBar.Value = _selectedStaff.Charisma;
        _charismaLabel.Text = $"Charisma: {_selectedStaff.Charisma:F0}";
        _negotiationBar.Value = _selectedStaff.Negotiation;
        _negotiationLabel.Text = $"Negotiation: {_selectedStaff.Negotiation:F0}";
        _discretionBar.Value = _selectedStaff.Discretion;
        _discretionLabel.Text = $"Discretion: {_selectedStaff.Discretion:F0}";

        _stressBar.Value = _selectedStaff.Stress;
        _stressLabel.Text = $"Stress: {_selectedStaff.Stress:F0}";
        _satisfactionBar.Value = _selectedStaff.Satisfaction;
        _satisfactionLabel.Text = $"Satisfaction: {_selectedStaff.Satisfaction:F0}";
        _traumaBar.Value = _selectedStaff.Trauma;
        _traumaLabel.Text = $"Trauma: {_selectedStaff.Trauma:F0}";

        // Button state refresh
        _btnAssign.Disabled = _selectedStaff.IsBurningOut;
        _btnTerminate.Disabled = false;
    }

    // ── UI Helpers ─────────────────────────────────────────────────────

    private (ProgressBar, Label) MakeStatRow(string name, Color color)
    {
        var hbox = new HBoxContainer();
        hbox.AddThemeConstantOverride("separation", 12);

        var label = MakeLabel($"{name}: 0", 13, Colors.White);
        label.CustomMinimumSize = new Vector2(120, 0);
        hbox.AddChild(label);

        var bar = new ProgressBar();
        bar.CustomMinimumSize = new Vector2(200, 16);
        bar.MinValue = 0;
        bar.MaxValue = 100;
        bar.ShowPercentage = false;

        var fill = new StyleBoxFlat
        {
            BgColor = color,
            CornerRadiusTopLeft = 3, CornerRadiusTopRight = 3,
            CornerRadiusBottomLeft = 3, CornerRadiusBottomRight = 3
        };
        bar.AddThemeStyleboxOverride("fill", fill);
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color(0.1f, 0.1f, 0.12f)
        });

        hbox.AddChild(bar);
        _cardContainer.AddChild(hbox);
        return (bar, label);
    }

    private static Label MakeLabel(string text, int size, Color color)
    {
        var l = new Label();
        l.Text = text;
        l.AddThemeFontSizeOverride("font_size", size);
        l.AddThemeColorOverride("font_color", color);
        return l;
    }

    private static Label MakeSectionHeader(string text)
    {
        var l = new Label();
        l.Text = text;
        l.AddThemeFontSizeOverride("font_size", 16);
        l.AddThemeColorOverride("font_color", new Color(1f, 0.8f, 0.3f));
        return l;
    }

    private static Button MakeActionButton(string text, Color color)
    {
        var btn = new Button();
        btn.Text = text;
        btn.AddThemeFontSizeOverride("font_size", 13);
        var style = new StyleBoxFlat { BgColor = color, CornerRadiusTopLeft=4,CornerRadiusTopRight=4,CornerRadiusBottomLeft=4,CornerRadiusBottomRight=4 };
        btn.AddThemeStyleboxOverride("normal", style);
        return btn;
    }
}
