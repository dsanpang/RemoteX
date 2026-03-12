using System;
using System.IO;
using System.Text.Json;

namespace RemoteX;

internal sealed class AppUiState
{
    public double WindowWidth { get; set; } = 1140;
    public double WindowHeight { get; set; } = 720;
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public bool IsMaximized { get; set; }
    public bool IsSidebarCollapsed { get; set; }
    public double SidebarExpandedWidth { get; set; } = 268;
    public int LastSelectedServerId { get; set; }
    public int ActivePanelIndex { get; set; } = 0;
}

internal sealed class UiStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _stateFilePath;

    public UiStateStore(string? stateFilePath = null)
    {
        _stateFilePath = string.IsNullOrWhiteSpace(stateFilePath)
            ? AppPaths.UiState
            : stateFilePath;
    }

    public AppUiState Load()
    {
        try
        {
            if (!File.Exists(_stateFilePath))
                return new AppUiState();

            var json = File.ReadAllText(_stateFilePath);
            var state = JsonSerializer.Deserialize<AppUiState>(json, JsonOptions) ?? new AppUiState();
            return Normalize(state);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"load ui state failed: {ex.Message}");
            return new AppUiState();
        }
    }

    public void Save(AppUiState state)
    {
        try
        {
            var normalized = Normalize(state);
            var dir = Path.GetDirectoryName(_stateFilePath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(normalized, JsonOptions);
            File.WriteAllText(_stateFilePath, json);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"save ui state failed: {ex.Message}");
        }
    }

    private static AppUiState Normalize(AppUiState state)
    {
        state.WindowWidth = Clamp(state.WindowWidth, 840, 5000, 1140);
        state.WindowHeight = Clamp(state.WindowHeight, 540, 4000, 720);
        state.SidebarExpandedWidth = Clamp(state.SidebarExpandedWidth, 180, 500, 268);
        if (!IsFinite(state.WindowLeft)) state.WindowLeft = double.NaN;
        if (!IsFinite(state.WindowTop)) state.WindowTop = double.NaN;
        if (state.LastSelectedServerId < 0) state.LastSelectedServerId = 0;
        state.ActivePanelIndex = Math.Clamp(state.ActivePanelIndex, 0, 4);
        return state;
    }

    private static double Clamp(double value, double min, double max, double fallback)
    {
        if (!IsFinite(value)) return fallback;
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }

    private static bool IsFinite(double value)
        => !(double.IsNaN(value) || double.IsInfinity(value));
}
