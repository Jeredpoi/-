using System;
using System.Collections.Generic;
using System.Reflection;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Isle.Services;

namespace Isle.Helpers
{
    /// <summary>Линейная иконка 24×24 (контур), цвет берётся из Foreground родителя.</summary>
    public class Icon : FrameworkElement
    {
        private static readonly Dictionary<string, Geometry> Cache = new();

        public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
            nameof(Kind), typeof(string), typeof(Icon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
            nameof(Data), typeof(string), typeof(Icon),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ForegroundProperty =
            TextElement.ForegroundProperty.AddOwner(typeof(Icon),
                new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty StrokeWidthProperty = DependencyProperty.Register(
            nameof(StrokeWidth), typeof(double), typeof(Icon),
            new FrameworkPropertyMetadata(1.75, FrameworkPropertyMetadataOptions.AffectsRender));

        public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
        public string Data { get => (string)GetValue(DataProperty); set => SetValue(DataProperty, value); }
        public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
        public double StrokeWidth { get => (double)GetValue(StrokeWidthProperty); set => SetValue(StrokeWidthProperty, value); }

        public Icon()
        {
            Width = 16; Height = 16;
            SnapsToDevicePixels = true;
        }

        private string Resolve()
        {
            if (!string.IsNullOrEmpty(Data)) return Data;
            if (string.IsNullOrEmpty(Kind)) return null;
            var f = typeof(Icons).GetField(Kind, BindingFlags.Public | BindingFlags.Static);
            return f?.GetValue(null) as string;
        }

        public static Geometry GeometryFor(string data)
        {
            if (string.IsNullOrEmpty(data)) return null;
            if (Cache.TryGetValue(data, out var g)) return g;
            try { g = Geometry.Parse(data); g.Freeze(); }
            catch { g = null; }
            Cache[data] = g;
            return g;
        }

        protected override void OnRender(DrawingContext dc)
        {
            var g = GeometryFor(Resolve());
            if (g == null) return;
            double s = Math.Min(ActualWidth, ActualHeight) / 24.0;
            if (s <= 0) return;
            var pen = new Pen(Foreground, StrokeWidth)
            {
                StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round
            };
            dc.PushTransform(new TranslateTransform((ActualWidth - 24 * s) / 2, (ActualHeight - 24 * s) / 2));
            dc.PushTransform(new ScaleTransform(s, s));
            dc.DrawGeometry(null, pen, g);
            dc.Pop();
            dc.Pop();
        }
    }
}

namespace Isle.Helpers
{
    /// <summary>{h:IconFor Home} — иконка для подстановки в Tag/Content прямо в XAML.</summary>
    [System.Windows.Markup.MarkupExtensionReturnType(typeof(Icon))]
    public class IconFor : System.Windows.Markup.MarkupExtension
    {
        public string Kind { get; set; }
        public double Size { get; set; } = 16;
        public IconFor() { }
        public IconFor(string kind) { Kind = kind; }
        public override object ProvideValue(System.IServiceProvider serviceProvider) => new Icon { Kind = Kind, Width = Size, Height = Size };
    }
}
