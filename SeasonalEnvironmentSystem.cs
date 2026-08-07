using Godot;
using System;

public enum Season { Spring, Summer, Autumn, Winter }
public enum WeatherCondition { Clear, Rain, Blizzard, Heatwave, Fog, Storm }

/// <summary>
/// Manages weather and seasonal effects. Seasons cycle every 90 days.
/// Dynamic weather conditions apply footfall modifiers:
/// Blizzards drop low-tier footfall 60% but increase VIP stays.
/// Heatwaves reduce daytime traffic. Storms boost security room demand.
/// </summary>
public partial class SeasonalEnvironmentSystem : Node
{
    [Signal] public delegate void OnSeasonChangedEventHandler(int oldSeason, int newSeason, string name);
    [Signal] public delegate void OnWeatherChangedEventHandler(int oldWeather, int newWeather, string name);

    private Season _season = Season.Spring;
    private WeatherCondition _weather = WeatherCondition.Clear;
    private int _daysInSeason;
    private int _weatherChangeDay;
    private readonly RandomNumberGenerator _rng = new();

    // Footfall modifiers by client tier
    public float LowTierFootfallMod { get; private set; } = 1f;
    public float MidTierFootfallMod { get; private set; } = 1f;
    public float VipFootfallMod { get; private set; } = 1f;

    // Additional modifiers
    public float DiscretionRequirementMod { get; private set; } = 1f;
    public float StaffStressMod { get; private set; } = 1f;
    public float RoomMaintenanceMod { get; private set; } = 1f;

    public Season CurrentSeason => _season;
    public WeatherCondition CurrentWeather => _weather;
    public int DaysInSeason => _daysInSeason;

    public override void _Ready()
    {
        _rng.Randomize();
        if (GameStateManager.Instance != null)
            GameStateManager.Instance.OnDailyTick += OnDailyTick;
        ChangeSeason(Season.Spring);
        GD.Print("[SeasonalEnv] Initialized.");
    }

    private void OnDailyTick(double cash, float rep, float heat, float sent)
    {
        _daysInSeason++;

        // Season change every 90 days
        if (_daysInSeason >= 90) CycleSeason();

        // Weather change check (every 3-7 days)
        if ((GameStateManager.Instance?.DayCount ?? 0) >= _weatherChangeDay)
            RollWeather();
    }

    private void CycleSeason()
    {
        Season next = _season switch
        {
            Season.Spring => Season.Summer,
            Season.Summer => Season.Autumn,
            Season.Autumn => Season.Winter,
            _ => Season.Spring
        };
        ChangeSeason(next);
    }

    private void ChangeSeason(Season newSeason)
    {
        var old = _season;
        _season = newSeason;
        _daysInSeason = 0;

        EmitSignal(SignalName.OnSeasonChanged, (int)old, (int)newSeason, newSeason.ToString());
        GD.Print($"[SeasonalEnv] Season: {newSeason}.");
    }

    private void RollWeather()
    {
        var old = _weather;

        // Season-appropriate weather probabilities
        float r = _rng.Randf();
        _weather = _season switch
        {
            Season.Summer => r < 0.4f ? WeatherCondition.Clear : r < 0.7f ? WeatherCondition.Heatwave : r < 0.9f ? WeatherCondition.Rain : WeatherCondition.Storm,
            Season.Winter => r < 0.3f ? WeatherCondition.Clear : r < 0.5f ? WeatherCondition.Blizzard : r < 0.75f ? WeatherCondition.Fog : WeatherCondition.Rain,
            Season.Autumn => r < 0.4f ? WeatherCondition.Clear : r < 0.7f ? WeatherCondition.Rain : r < 0.9f ? WeatherCondition.Fog : WeatherCondition.Storm,
            _ => r < 0.5f ? WeatherCondition.Clear : r < 0.8f ? WeatherCondition.Rain : WeatherCondition.Fog
        };

        _weatherChangeDay = (GameStateManager.Instance?.DayCount ?? 0) + 3 + (int)(_rng.Randi() % 5u);

        ApplyWeatherModifiers();

        EmitSignal(SignalName.OnWeatherChanged, (int)old, (int)_weather, _weather.ToString());
        GD.Print($"[SeasonalEnv] Weather: {_weather} (next change day {_weatherChangeDay}).");
    }

    private void ApplyWeatherModifiers()
    {
        // Reset to base
        LowTierFootfallMod = MidTierFootfallMod = VipFootfallMod = 1f;
        DiscretionRequirementMod = StaffStressMod = RoomMaintenanceMod = 1f;

        // Season base modifiers
        switch (_season)
        {
            case Season.Summer:
                LowTierFootfallMod *= 1.2f;  // more people out
                VipFootfallMod *= 0.8f;       // VIPs on vacation
                break;
            case Season.Winter:
                LowTierFootfallMod *= 0.7f;   // cold keeps people home
                VipFootfallMod *= 1.3f;        // VIPs seek indoor luxury
                RoomMaintenanceMod *= 1.2f;    // heating costs
                break;
            case Season.Autumn:
                MidTierFootfallMod *= 1.1f;
                break;
        }

        // Weather modifiers (overlay on season)
        switch (_weather)
        {
            case WeatherCondition.Rain:
                LowTierFootfallMod *= 0.7f;
                VipFootfallMod *= 1.1f;   // VIPs still come in cars
                RoomMaintenanceMod *= 1.05f;
                break;
            case WeatherCondition.Blizzard:
                LowTierFootfallMod *= 0.4f;    // −60% low-tier
                MidTierFootfallMod *= 0.6f;
                VipFootfallMod *= 1.4f;         // VIPs stay in luxury suites
                StaffStressMod *= 1.2f;
                RoomMaintenanceMod *= 1.3f;     // heating + snow damage
                break;
            case WeatherCondition.Heatwave:
                LowTierFootfallMod *= 0.8f;    // too hot to go out
                MidTierFootfallMod *= 0.9f;
                DiscretionRequirementMod *= 1.15f; // people are irritable, more careful
                StaffStressMod *= 1.15f;
                break;
            case WeatherCondition.Fog:
                DiscretionRequirementMod *= 0.85f; // easier to hide in fog
                VipFootfallMod *= 1.15f;            // fog = privacy for VIPs
                break;
            case WeatherCondition.Storm:
                LowTierFootfallMod *= 0.5f;
                VipFootfallMod *= 1.2f;    // VIPs seek shelter in luxury
                RoomMaintenanceMod *= 1.2f;
                StaffStressMod *= 1.1f;
                break;
        }

        GD.Print($"[SeasonalEnv] Modifiers: Low×{LowTierFootfallMod:F1} Mid×{MidTierFootfallMod:F1} VIP×{VipFootfallMod:F1}");
    }

    /// <summary>Get a text summary of current conditions.</summary>
    public string GetWeatherReport()
    {
        string seasonDesc = _season switch
        {
            Season.Spring => "Mild spring breeze",
            Season.Summer => "Warm summer air",
            Season.Autumn => "Crisp autumn chill",
            Season.Winter => "Cold winter frost",
            _ => ""
        };
        string weatherDesc = _weather switch
        {
            WeatherCondition.Clear => "Clear skies",
            WeatherCondition.Rain => "Heavy rain",
            WeatherCondition.Blizzard => "Severe blizzard",
            WeatherCondition.Heatwave => "Scorching heatwave",
            WeatherCondition.Fog => "Dense fog",
            WeatherCondition.Storm => "Violent storm",
            _ => ""
        };
        return $"{weatherDesc} — {seasonDesc} (Day {_daysInSeason}/90)\n" +
               $"Footfall: Low×{LowTierFootfallMod:F1} Mid×{MidTierFootfallMod:F1} VIP×{VipFootfallMod:F1}";
    }

    public override string ToString() =>
        $"[SeasonalEnv] {_season} {_weather} Day{_daysInSeason}";
}
