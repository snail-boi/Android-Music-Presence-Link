using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace musicpresense
{
    /// <summary>
    /// Animates a <see cref="GridLength"/> value between two pixel widths.
    /// Required because WPF has no built-in GridLength animation type.
    /// Only pixel-unit GridLengths are supported (Auto/Star are not interpolated).
    /// </summary>
    public class GridLengthAnimation : AnimationTimeline
    {
        public override Type TargetPropertyType => typeof(GridLength);

        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();

        // ── Dependency properties ────────────────────────────────────────────

        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation),
                new PropertyMetadata(new GridLength(0)));

        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation),
                new PropertyMetadata(new GridLength(0)));

        public static readonly DependencyProperty EasingFunctionProperty =
            DependencyProperty.Register(nameof(EasingFunction), typeof(IEasingFunction), typeof(GridLengthAnimation),
                new PropertyMetadata(null));

        public GridLength From
        {
            get => (GridLength)GetValue(FromProperty);
            set => SetValue(FromProperty, value);
        }

        public GridLength To
        {
            get => (GridLength)GetValue(ToProperty);
            set => SetValue(ToProperty, value);
        }

        public IEasingFunction? EasingFunction
        {
            get => (IEasingFunction?)GetValue(EasingFunctionProperty);
            set => SetValue(EasingFunctionProperty, value);
        }

        // ── Interpolation ────────────────────────────────────────────────────

        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
        {
            double progress = animationClock.CurrentProgress ?? 0;

            if (EasingFunction != null)
                progress = EasingFunction.Ease(progress);

            double from = From.IsAbsolute ? From.Value : 0;
            double to = To.IsAbsolute ? To.Value : 0;
            double value = from + (to - from) * progress;

            return new GridLength(Math.Max(0, value), GridUnitType.Pixel);
        }
    }
}
