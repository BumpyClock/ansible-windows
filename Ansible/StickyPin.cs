using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Ansible;

/// <summary>
/// Holds one element of a <see cref="PageCanvas"/> on screen while the page scrolls, like CSS <c>position: sticky</c>.
/// The element sticks <c>top</c> DIPs below the viewport top. An element taller than the viewport scrolls until its
/// bottom is <c>bottom</c> DIPs above the visible bottom, then sticks there. It never travels past the bottom of
/// <c>bounds</c>. The compositor applies the offset from the scroll position, so it does not lag behind touchpad or
/// inertia scrolling. XAML hit testing and TransformToVisual follow the composited Translation. Create pins with
/// <see cref="PageCanvas.Pin"/>; dispose to return the element to its layout position.
/// </summary>
internal sealed class StickyPin : IDisposable
{
    // The stuck indicator fades in over this many DIPs of scrolling past the stick point.
    private const float IndicatorFadeDistance = 8;

    private readonly PageCanvas _canvas;
    private readonly FrameworkElement _bounds;
    private readonly double _top;
    private readonly double _bottom;
    private readonly UIElement? _indicator;
    private CompositionPropertySet? _parameters;
    private Visual? _visual;
    private Visual? _indicatorVisual;
    private float _start = float.NaN;
    private float _range = float.NaN;

    internal StickyPin(PageCanvas canvas, FrameworkElement element, FrameworkElement bounds, double top, double bottom, UIElement? indicator)
    {
        _canvas = canvas;
        Element = element;
        _bounds = bounds;
        _top = top;
        _bottom = bottom;
        _indicator = indicator;
    }

    internal FrameworkElement Element { get; }

    /// <summary>True while the scroll offset holds the element away from its layout position.</summary>
    internal bool IsStuck(double verticalOffset) => _range > 0 && verticalOffset > _start;

    internal bool Contains(DependencyObject? target)
    {
        for (; target is not null; target = VisualTreeHelper.GetParent(target))
            if (ReferenceEquals(target, Element)) return true;
        return false;
    }

    /// <summary>
    /// Recomputes where sticking starts and how far the element may travel, in content coordinates. Positions come
    /// from the untranslated parent plus the layout offset, because the element's own transform includes the pin.
    /// </summary>
    internal void Update(CompositionPropertySet scroll, UIElement content, double visibleHeight)
    {
        if (VisualTreeHelper.GetParent(Element) is not UIElement parent || !_bounds.IsLoaded) return;
        var top = parent.TransformToVisual(content).TransformPoint(default).Y + Element.ActualOffset.Y;
        var height = Element.ActualHeight;
        var boundsBottom = _bounds.TransformToVisual(content).TransformPoint(default).Y + _bounds.ActualHeight;
        var overflow = Math.Max(0, height - (visibleHeight - _top - _bottom));
        var start = (float)(top - _top + overflow);
        var range = (float)Math.Max(0, boundsBottom - top - height);
        if (_parameters is null) Start(scroll);
        if (start == _start && range == _range) return;
        _start = start;
        _range = range;
        _parameters!.InsertScalar("Start", start);
        _parameters.InsertScalar("Range", range);
        // An element that cannot travel is never stuck, so its indicator stays hidden.
        _parameters.InsertScalar("Engaged", range > 0 ? 1 : 0);
    }

    private void Start(CompositionPropertySet scroll)
    {
        var compositor = scroll.Compositor;
        _parameters = compositor.CreatePropertySet();
        _parameters.InsertScalar("Start", 0);
        _parameters.InsertScalar("Range", 0);
        _parameters.InsertScalar("Engaged", 0);
        // The manipulation Translation.Y is minus the scroll offset; it is positive while overpanning at the top.
        ElementCompositionPreview.SetIsTranslationEnabled(Element, true);
        _visual = ElementCompositionPreview.GetElementVisual(Element);
        var offset = compositor.CreateExpressionAnimation("Clamp(-scroll.Translation.Y - pin.Start, 0, pin.Range)");
        offset.SetReferenceParameter("scroll", scroll);
        offset.SetReferenceParameter("pin", _parameters);
        _visual.StartAnimation("Translation.Y", offset);
        if (_indicator is null) return;
        _indicatorVisual = ElementCompositionPreview.GetElementVisual(_indicator);
        var opacity = compositor.CreateExpressionAnimation(
            $"Clamp((-scroll.Translation.Y - pin.Start) / {IndicatorFadeDistance}, 0, 1) * pin.Engaged");
        opacity.SetReferenceParameter("scroll", scroll);
        opacity.SetReferenceParameter("pin", _parameters);
        _indicatorVisual.StartAnimation("Opacity", opacity);
    }

    /// <summary>Stops the animations and returns the element to its layout position.</summary>
    internal void Stop()
    {
        if (_visual is not null)
        {
            _visual.StopAnimation("Translation.Y");
            _visual.Properties.InsertVector3("Translation", Vector3.Zero);
        }
        if (_indicatorVisual is not null)
        {
            _indicatorVisual.StopAnimation("Opacity");
            _indicatorVisual.Opacity = 0;
        }
        _parameters?.Dispose();
        _parameters = null;
        _visual = null;
        _indicatorVisual = null;
        _start = _range = float.NaN;
    }

    public void Dispose() => _canvas.Unpin(this);
}
