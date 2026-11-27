using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chaos.Display;

/// <summary>Display mode picked on the Settings page.</summary>
[JsonConverter(typeof(ResolutionConverter))]
public readonly record struct Resolution
{
    public const int GameWidth = 800, GameHeight = 600;

    Resolution(int width, int height, bool fullScreen)
    {
        Width = width;
        Height = height;
        IsFullScreen = fullScreen;
    }

    public static Resolution Default => default;

    public static Resolution FullScreen { get; } = new(0, 0, fullScreen: true);

    public static IReadOnlyList<Resolution> Windows { get; } =
    [
        new(1024, 768, false), new(1152, 864, false), new(1280, 960, false), new(1400, 1050, false),
        new(1600, 1200, false), new(2048, 1536, false), new(2400, 1800, false), new(3200, 2400, false),
    ];

    public int Width { get; }

    public int Height { get; }

    public bool IsFullScreen { get; }

    public bool IsDefault => !IsFullScreen && Width == 0;

    public bool IsWindow => Width > 0;

    public bool IsSharp => IsWindow && Width % GameWidth == 0 && Height == Width / GameWidth * GameHeight;

    public override string ToString() =>
        IsFullScreen ? "full" : IsWindow ? string.Create(CultureInfo.InvariantCulture, $"{Width}x{Height}") : "default";

    public static Resolution Parse(string? text)
    {
        string name = text?.Trim() ?? "";
        if (name.Equals("full", StringComparison.OrdinalIgnoreCase))
            return FullScreen;
        foreach (var window in Windows)
            if (name.Equals(window.ToString(), StringComparison.OrdinalIgnoreCase))
                return window;
        return Default;
    }
}

sealed class ResolutionConverter : JsonConverter<Resolution>
{
    public override bool HandleNull => true;

    public override Resolution Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return Resolution.Parse(reader.GetString());
        reader.Skip();
        return Resolution.Default;
    }

    public override void Write(Utf8JsonWriter writer, Resolution value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}
