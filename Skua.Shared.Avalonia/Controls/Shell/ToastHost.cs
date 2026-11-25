using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Skua.Shared.Avalonia.Theming;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Skua.Shared.Avalonia.Controls.Shell;

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
        if (ReferenceEquals(Current, this))
            Current = null;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    public static bool LooksLikeFailure(string text)
    {
        string lower = text.ToLowerInvariant();
        foreach (string word in FailureWords)
        {
            if (lower.Contains(word))
                return true;
        }
        return false;
    }

    public void Push(string text)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Push(text));
            return;
        }

        text = text.Trim();
        if (text.Length == 0)
            return;

        double now = _clock.Elapsed.TotalSeconds;
        if (_toasts.Count > 0 && _toasts[^1].Text == text && now - _toasts[^1].Born < 1)
            return;

        bool failure = LooksLikeFailure(text);
        var shift = new TranslateTransform();
        var bar = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, UiTokens.GetDouble(UiTokens.UiGapMd, 8), 0)
        };
        UiTokens.Bind(bar, Layoutable.HeightProperty, UiTokens.UiIconMd);
        bar.Bind(Border.BackgroundProperty, this.GetResourceObservable(failure ? UiTokens.SkuaDangerBrush : UiTokens.SkuaAccentBrush));

        var label = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
            VerticalAlignment = VerticalAlignment.Center
        };
        UiTokens.Bind(label, TextBlock.FontSizeProperty, UiTokens.UiFontBody);
        label.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(UiTokens.SkuaTextBrush));

        var card = new Border
        {
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { bar, label } },
            Padding = new Thickness(12, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            RenderTransform = shift,
            Opacity = 0
        };
        UiTokens.Bind(card, Border.CornerRadiusProperty, UiTokens.UiRadiusCard);
        UiTokens.Bind(card, Border.BorderThicknessProperty, UiTokens.UiBorderThin);
        card.Bind(Border.BackgroundProperty, this.GetResourceObservable(UiTokens.SkuaSurfaceBrush));
        card.Bind(Border.BorderBrushProperty, this.GetResourceObservable(UiTokens.SkuaBorderStrongBrush));

        _toasts.Add(new Toast { Text = text, View = card, Shift = shift, Born = now, Life = failure ? 7 : 4.5 });
        Children.Add(card);
        while (_toasts.Count > MostShown)
            Remove(_toasts[0]);

        Tick();
        _timer.Start();
    }

    private void Remove(Toast toast)
    {
        _toasts.Remove(toast);
        Children.Remove(toast.View);
    }

    private void Tick()
    {
        double now = _clock.Elapsed.TotalSeconds;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            if (now - _toasts[i].Born >= _toasts[i].Life)
                Remove(_toasts[i]);
        }

        if (_toasts.Count == 0)
        {
            _timer.Stop();
            return;
        }

        double lift = 12;
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            Toast toast = _toasts[i];
            double age = now - toast.Born;
            double enter = 1 - Math.Pow(1 - Math.Clamp(age / SlideIn, 0, 1), 3);
            double leave = Math.Clamp((toast.Life - age) / FadeOut, 0, 1);
            toast.View.Opacity = Math.Min(enter, leave);
            toast.View.Margin = new Thickness(0, 0, 12, lift);
            toast.Shift.X = ((1 - enter) + (1 - leave) * 0.5) * 46;
            lift += toast.View.DesiredSize.Height + 6;
        }
    }
}
