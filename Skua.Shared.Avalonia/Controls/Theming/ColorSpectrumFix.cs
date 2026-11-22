using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Reflection;

namespace Skua.Shared.Avalonia.Controls.Theming;

public static class ColorSpectrumFix
{
    private static readonly FieldInfo? s_isPointerPressedField =
        typeof(ColorSpectrum).GetField("_isPointerPressed", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? s_shouldShowLargeSelectionField =
        typeof(ColorSpectrum).GetField("_shouldShowLargeSelection", BindingFlags.NonPublic | BindingFlags.Instance);
    private static bool s_initialized;

    public static void Initialize()
    {
        if (s_initialized)
            return;
        s_initialized = true;

        Control.LoadedEvent.AddClassHandler<ColorSpectrum>((spectrum, _) =>
        {
            ResetPointerState(spectrum);
        }, handledEventsToo: true);

        Control.UnloadedEvent.AddClassHandler<ColorSpectrum>((spectrum, _) =>
        {
            ResetPointerState(spectrum);
        }, handledEventsToo: true);

        InputElement.PointerCaptureLostEvent.AddClassHandler<ColorSpectrum>((spectrum, _) =>
        {
            ResetPointerState(spectrum);
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);

        InputElement.PointerEnteredEvent.AddClassHandler<ColorSpectrum>((spectrum, e) =>
        {
            var point = e.GetCurrentPoint(spectrum);
            if (!point.Properties.IsLeftButtonPressed)
            {
                ResetPointerState(spectrum);
            }
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);

        InputElement.PointerMovedEvent.AddClassHandler<ColorSpectrum>((spectrum, e) =>
        {
            var point = e.GetCurrentPoint(spectrum);
            if (!point.Properties.IsLeftButtonPressed && IsPointerPressed(spectrum))
            {
                ResetPointerState(spectrum);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        InputElement.PointerReleasedEvent.AddClassHandler<ColorSpectrum>((spectrum, _) =>
        {
            ResetPointerState(spectrum);
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public static bool IsPointerPressed(ColorSpectrum spectrum)
    {
        return s_isPointerPressedField?.GetValue(spectrum) as bool? == true;
    }

    public static void ResetPointerState(ColorSpectrum spectrum)
    {
        try
        {
            s_isPointerPressedField?.SetValue(spectrum, false);
            s_shouldShowLargeSelectionField?.SetValue(spectrum, false);
            spectrum.Classes.Set(":pressed", false);
        }
        catch
        {
        }
    }
}
