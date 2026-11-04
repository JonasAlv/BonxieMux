using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using System.Reflection;

namespace Skua.Shared.Avalonia.Controls.Theming;

/// <summary>
/// Fixes an Avalonia ColorSpectrum pointer capture bug where dismissing or closing the flyout/popup
/// while a mouse button is pressed leaves ColorSpectrum's internal _isPointerPressed field stuck at true.
/// When reopened, any hover would register as a mouse drag/click and immediately trigger selections.
/// </summary>
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

        // When ColorSpectrum loads (flyout/popup opens):
        Control.LoadedEvent.AddClassHandler<ColorSpectrum>((spectrum, _) =>
        {
            ResetPointerState(spectrum);
        }, handledEventsToo: true);

        // When ColorSpectrum unloads (flyout/popup closes):
        Control.UnloadedEvent.AddClassHandler<ColorSpectrum>((spectrum, _) =>
        {
            ResetPointerState(spectrum);
        }, handledEventsToo: true);

        // When pointer capture is lost on ColorSpectrum:
        InputElement.PointerCaptureLostEvent.AddClassHandler<ColorSpectrum>((spectrum, _) =>
        {
            ResetPointerState(spectrum);
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);

        // When pointer enters ColorSpectrum:
        InputElement.PointerEnteredEvent.AddClassHandler<ColorSpectrum>((spectrum, e) =>
        {
            var point = e.GetCurrentPoint(spectrum);
            if (!point.Properties.IsLeftButtonPressed)
            {
                ResetPointerState(spectrum);
            }
        }, RoutingStrategies.Bubble | RoutingStrategies.Tunnel, handledEventsToo: true);

        // When pointer moves over ColorSpectrum:
        // Use Tunneling strategy to intercept before ColorSpectrum's internal InputTarget_PointerMoved executes.
        InputElement.PointerMovedEvent.AddClassHandler<ColorSpectrum>((spectrum, e) =>
        {
            var point = e.GetCurrentPoint(spectrum);
            if (!point.Properties.IsLeftButtonPressed && IsPointerPressed(spectrum))
            {
                ResetPointerState(spectrum);
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);

        // When pointer is released:
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
            // Best effort safeguard
        }
    }
}
