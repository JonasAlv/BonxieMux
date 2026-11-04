using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Skua.Manager.Avalonia.Controls;

/// <summary>Non-blocking status messages shown over the Manager.</summary>
public sealed class ToastHost : Panel
{
    private const double SlideIn = 0.28;
    private const double FadeOut = 0.30;
    private const int MostShown = 4;
    private sealed class Toast
    {
        public required string Text { get; init; }
        public required Border View { get; init; }
        public required TranslateTransform Shift { get; init; }
        public required double Born { get; init; }
        public required double Life { get; init; }
    }

    private static readonly string[] FailureWords = ["could not", "couldn't", "cannot", "can't", "failed", "error", "no answer", "not found", "unable"];
    private readonly List<Toast> _toasts = new();
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    public static ToastHost? Current { get; private set; }

    public ToastHost()
    {
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) => Tick());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Current = this;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (ReferenceEquals(Current, this)) Current = null;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public static bool LooksLikeFailure(string text)
    {
        string lower = text.ToLowerInvariant();
        foreach (string word in FailureWords)
            if (lower.Contains(word)) return true;
        return false;
    }

    public void Push(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Push(text)); return; }
        text = text.Trim();
        if (text.Length == 0) return;
        double now = _clock.Elapsed.TotalSeconds;
        if (_toasts.Count > 0 && _toasts[^1].Text == text && now - _toasts[^1].Born < 1) return;
        bool failure = LooksLikeFailure(text);
        var shift = new TranslateTransform();
        var bar = new Border { Width = 4, Height = 18, CornerRadius = new CornerRadius(2), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        bar.Bind(Border.BackgroundProperty, this.GetResourceObservable(failure ? "SkuaDangerBrush" : "SkuaAccentBrush"));
        var label = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 320, VerticalAlignment = VerticalAlignment.Center };
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("SkuaTextBrush"));
        var card = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { bar, label } },
            Padding = new Thickness(14, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransform = shift, Opacity = 0,
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 16, OffsetY = 4, Color = Color.FromArgb(90, 0, 0, 0) })
        };
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable("SkuaSurfaceBrush"));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("SkuaBorderBrush"));
        _toasts.Add(new Toast { Text = text, View = card, Shift = shift, Born = now, Life = failure ? 7 : 4.5 });
        Children.Add(card);
        while (_toasts.Count > MostShown) Remove(_toasts[0]);
        Tick();
        _timer.Start();
    }

    private void Remove(Toast toast) { _toasts.Remove(toast); Children.Remove(toast.View); }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        for (int i = _toasts.Count - 1; i >= 0; i--)
            if (now - _toasts[i].Born >= _toasts[i].Life) Remove(_toasts[i]);
        if (_toasts.Count == 0) { _timer.Stop(); return; }
        double lift = 16;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            Toast toast = _toasts[i];
            double age = now - toast.Born;
            double enter = 1 - Math.Pow(1 - Math.Clamp(age / SlideIn, 0, 1), 3);
            double leave = Math.Clamp((toast.Life - age) / FadeOut, 0, 1);
            toast.View.Opacity = Math.Min(enter, leave);
            toast.View.Margin = new Thickness(0, 0, 16, lift);
            toast.Shift.X = ((1 - enter) + (1 - leave) * 0.5) * 46;
            lift += toast.View.DesiredSize.Height + 8;
        }
    }
}
