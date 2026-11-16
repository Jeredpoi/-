using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Helpers;
using Isle.Services;

namespace Isle.Views
{
    /// <summary>
    /// Просмотр картинки из буфера: клик по миниатюре — картинка плавно вырастает из неё на всю вкладку
    /// (сначала уменьшенная копия, следом — полное разрешение). Колёсико — масштаб к курсору, перетаскивание —
    /// сдвиг, двойной клик — приблизить/вернуть, Esc или клик по фону — плавно обратно в миниатюру.
    /// </summary>
    public partial class ClipboardView
    {
        private ClipItem _viewItem;
        private FrameworkElement _viewThumb;
        private Rect _viewFrom, _viewTo;            // откуда (миниатюра) и куда (вписанная картинка), в координатах ViewerCanvas
        private double _animT0, _animDur;
        private bool _animOpening, _animating;
        private Size _viewPx;                       // размер картинки в пикселях
        private Matrix _zoom = Matrix.Identity;     // масштаб и сдвиг внутри рамки
        private Point? _panFrom;
        private int _viewVersion;

        private string ViewPath(ClipItem it) => it.IsImage ? it.ImagePath : it.IsFiles ? it.Files.FirstOrDefault(File.Exists) : null;

        private void OnThumbClick(object sender, MouseButtonEventArgs e)
        {
            if (_dragged) return; // это было перетаскивание
            if ((sender as FrameworkElement)?.Tag is not ClipItem item || ViewPath(item) == null) return;
            e.Handled = true;      // не вставлять запись
            _pressed = null;
            OpenViewer(item, (FrameworkElement)sender);
        }

        private void OpenViewer(ClipItem item, FrameworkElement thumb)
        {
            _viewItem = item;
            _viewThumb = thumb;
            int version = ++_viewVersion;
            Viewer.Visibility = Visibility.Visible;
            Viewer.UpdateLayout();

            // размер картинки: из миниатюры (пропорции те же), потом уточним по полной
            var src = item.Thumb as BitmapSource;
            _viewPx = src != null ? new Size(src.PixelWidth, src.PixelHeight) : new Size(16, 9);
            if (item.IsImage && item.Text.StartsWith("Изображение ") && item.Text.Split(' ', '×') is var parts && parts.Length >= 3
                && int.TryParse(parts[1], out int pw) && int.TryParse(parts[2], out int ph))
                _viewPx = new Size(pw, ph);
            ViewerImage.Source = item.Thumb;
            ResetZoom();

            _viewFrom = ThumbRect(thumb);
            _viewTo = FitRect();
            ViewerSaveBtn.Visibility = item.IsImage ? Visibility.Visible : Visibility.Collapsed;
            ViewerInfo.Text = (int)_viewPx.Width + "×" + (int)_viewPx.Height + " · колёсико — масштаб · Esc — закрыть";
            StartViewerAnim(opening: true);
            Journal.Info("clipboard", "просмотр картинки " + (int)_viewPx.Width + "×" + (int)_viewPx.Height);

            // клавиатура для Esc и колёсика: окну нужен фокус (переднее окно для вставки при этом не теряется)
            AppHost.Window.FocusIsland();
            Viewer.Focusable = true;
            Viewer.Focus();

            // полное разрешение — в фоне, чтобы анимация не ждала декодирования
            var path = ViewPath(item);
            _ = Task.Run(() =>
            {
                try
                {
                    var b = new BitmapImage();
                    b.BeginInit();
                    b.CacheOption = BitmapCacheOption.OnLoad;
                    b.UriSource = new Uri(path);
                    b.EndInit();
                    b.Freeze();
                    return (BitmapSource)b;
                }
                catch { return null; }
            }).ContinueWith(t => Dispatcher.InvokeAsync(() =>
            {
                if (version != _viewVersion || t.Result == null) return;
                _viewPx = new Size(t.Result.PixelWidth, t.Result.PixelHeight);
                ViewerImage.Source = t.Result;
                ViewerInfo.Text = t.Result.PixelWidth + "×" + t.Result.PixelHeight + " · колёсико — масштаб · Esc — закрыть";
                if (!_animating) { _viewTo = FitRect(); PlaceFrame(_viewTo); }
                else if (_animOpening) _viewTo = FitRect();
            }));
        }

        /// <summary>Где сейчас миниатюра (в координатах слоя просмотра).</summary>
        private Rect ThumbRect(FrameworkElement thumb)
        {
            try
            {
                if (thumb != null && thumb.IsVisible && PresentationSource.FromVisual(thumb) != null)
                    return thumb.TransformToVisual(ViewerCanvas).TransformBounds(new Rect(thumb.RenderSize));
            }
            catch { }
            var c = new Point(ViewerCanvas.ActualWidth / 2, ViewerCanvas.ActualHeight / 2);
            return new Rect(c.X - 28, c.Y - 18, 56, 36);
        }

        /// <summary>Картинка, вписанная во вкладку над панелью кнопок; маленькую не растягиваем больше чем вдвое.</summary>
        private Rect FitRect()
        {
            double aw = Math.Max(50, ViewerCanvas.ActualWidth - 24), ah = Math.Max(50, ViewerCanvas.ActualHeight - 24 - 58);
            var dpi = VisualTreeHelper.GetDpi(this);
            double natW = _viewPx.Width / dpi.DpiScaleX, natH = _viewPx.Height / dpi.DpiScaleY;
            double k = Math.Min(Math.Min(aw / natW, ah / natH), 2);
            double w = natW * k, h = natH * k;
            return new Rect((ViewerCanvas.ActualWidth - w) / 2, 12 + (ah - h) / 2, w, h);
        }

        private void PlaceFrame(Rect r)
        {
            Canvas.SetLeft(ViewerFrame, r.X); Canvas.SetTop(ViewerFrame, r.Y);
            ViewerFrame.Width = Math.Max(1, r.Width); ViewerFrame.Height = Math.Max(1, r.Height);
            // скругление: у миниатюры 8, у большой картинки 12
            double rad = 8 + 4 * Math.Clamp((r.Width - _viewFrom.Width) / Math.Max(1, _viewTo.Width - _viewFrom.Width), 0, 1);
            ViewerFrame.Clip = new RectangleGeometry(new Rect(0, 0, Math.Max(1, r.Width), Math.Max(1, r.Height)), rad, rad);
        }

        // ---------- анимация: в такт монитору (общий кадр приложения) ----------
        private void StartViewerAnim(bool opening)
        {
            _animOpening = opening;
            _animT0 = Environment.TickCount64;
            _animDur = opening ? 340 : 260;
            if (!_animating) { _animating = true; FrameClock.Tick += OnViewerFrame; }
            OnViewerFrame(0);
        }

        private static double EaseOut(double u) => 1 - Math.Pow(1 - Math.Clamp(u, 0, 1), 4);
        private static double EaseInOut(double u) { u = Math.Clamp(u, 0, 1); return u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2; }

        private void OnViewerFrame(double _)
        {
            double u = (Environment.TickCount64 - _animT0) / _animDur;
            double k = _animOpening ? EaseOut(u) : EaseInOut(u);
            var (a, b) = _animOpening ? (_viewFrom, _viewTo) : (_viewTo, _viewFrom);
            var r = new Rect(a.X + (b.X - a.X) * k, a.Y + (b.Y - a.Y) * k, a.Width + (b.Width - a.Width) * k, a.Height + (b.Height - a.Height) * k);
            PlaceFrame(r);
            double show = _animOpening ? k : 1 - k;
            ViewerBack.Opacity = show;
            ViewerBar.Opacity = _animOpening ? Math.Clamp((u - 0.4) / 0.6, 0, 1) : 1 - Math.Clamp(u * 2, 0, 1);
            // миниатюра «улетает» в просмотр — на месте её не видно, пока картинка открыта
            if (_viewThumb != null) _viewThumb.Opacity = u >= 1 && !_animOpening ? 1 : 0;
            if (u < 1) return;
            FrameClock.Tick -= OnViewerFrame;
            _animating = false;
            if (!_animOpening) FinishClose();
        }

        private void CloseViewer(bool instant = false)
        {
            if (Viewer.Visibility != Visibility.Visible || (_animating && !_animOpening)) return;
            _viewVersion++;
            if (instant) { if (_animating) { FrameClock.Tick -= OnViewerFrame; _animating = false; } FinishClose(); return; }
            // обратно — к миниатюре, если строка ещё на месте (её могли прокрутить)
            var thumb = FindThumb(_viewItem) ?? _viewThumb;
            _viewThumb = thumb;
            _viewFrom = ThumbRect(thumb);
            _viewTo = new Rect(Canvas.GetLeft(ViewerFrame), Canvas.GetTop(ViewerFrame), ViewerFrame.Width, ViewerFrame.Height);
            ResetZoom();
            StartViewerAnim(opening: false);
        }

        private void FinishClose()
        {
            if (_viewThumb != null) _viewThumb.Opacity = 1;
            Viewer.Visibility = Visibility.Collapsed;
            ViewerImage.Source = null; // полное разрешение 4K — десятки мегабайт, не держим
            _viewItem = null; _viewThumb = null;
            MemoryTrim.Soon("просмотр картинки");
        }

        /// <summary>Миниатюра записи в списке (список разбит на дни — ищем по дереву, а не через генератор строк).</summary>
        private FrameworkElement FindThumb(ClipItem item) => item == null ? null : FindThumbIn(List, item);

        private static FrameworkElement FindThumbIn(DependencyObject d, ClipItem item)
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            {
                var ch = VisualTreeHelper.GetChild(d, i);
                if (ch is FrameworkElement { Name: "imgIcon" } fe && ReferenceEquals(fe.Tag, item)) return fe;
                if (FindThumbIn(ch, item) is FrameworkElement f) return f;
            }
            return null;
        }

        private void OnViewerBackClick(object s, MouseButtonEventArgs e) { e.Handled = true; CloseViewer(); }
        private void OnViewerClose(object s, RoutedEventArgs e) => CloseViewer();

        /// <summary>Esc сначала закрывает просмотр, а не сворачивает чёлку.</summary>
        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            if (Viewer.Visibility == Visibility.Visible && e.Key == Key.Escape) { CloseViewer(); e.Handled = true; return; }
            base.OnPreviewKeyDown(e);
        }

        // ---------- масштаб и сдвиг ----------
        private void ResetZoom()
        {
            _zoom = Matrix.Identity;
            ViewerImage.RenderTransform = new MatrixTransform(_zoom);
            ViewerFrame.Cursor = Cursors.Arrow;
        }

        private void ApplyZoom()
        {
            double w = ViewerFrame.ActualWidth, h = ViewerFrame.ActualHeight, s = _zoom.M11;
            if (s <= 1.001) { ResetZoom(); return; }
            // не даём увести картинку за край рамки
            _zoom.OffsetX = Math.Clamp(_zoom.OffsetX, w - w * s, 0);
            _zoom.OffsetY = Math.Clamp(_zoom.OffsetY, h - h * s, 0);
            ViewerImage.RenderTransform = new MatrixTransform(_zoom);
            ViewerFrame.Cursor = Cursors.SizeAll;
        }

        private void ZoomAt(Point p, double factor)
        {
            double s = _zoom.M11, target = Math.Clamp(s * factor, 1, 8);
            if (Math.Abs(target - s) < 0.0001) return;
            _zoom.ScaleAt(target / s, target / s, p.X, p.Y);
            ApplyZoom();
        }

        private void OnViewerWheel(object s, MouseWheelEventArgs e)
        {
            e.Handled = true;
            if (_animating) return;
            ZoomAt(e.GetPosition(ViewerFrame), e.Delta > 0 ? 1.25 : 0.8);
        }

        private void OnViewerDown(object s, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (e.ClickCount == 2) { if (_zoom.M11 > 1.01) ResetZoom(); else ZoomAt(e.GetPosition(ViewerFrame), 2.5); return; }
            if (_zoom.M11 <= 1.001) return;
            _panFrom = e.GetPosition(ViewerFrame);
            ViewerFrame.CaptureMouse();
        }

        private void OnViewerMove(object s, MouseEventArgs e)
        {
            if (_panFrom is not Point from || e.LeftButton != MouseButtonState.Pressed) return;
            var p = e.GetPosition(ViewerFrame);
            _zoom.Translate(p.X - from.X, p.Y - from.Y);
            _panFrom = p;
            ApplyZoom();
        }

        private void OnViewerUp(object s, MouseButtonEventArgs e)
        {
            e.Handled = true; // клик по самой картинке не закрывает просмотр
            if (_panFrom == null) return;
            _panFrom = null;
            ViewerFrame.ReleaseMouseCapture();
        }

        // ---------- кнопки ----------
        private void OnViewerCopy(object s, RoutedEventArgs e)
        {
            if (_viewItem == null) return;
            Svc.Paste(_viewItem, IntPtr.Zero, false);
            ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Скопировано", Icon = Icons.Copy, Important = true, Duration = TimeSpan.FromSeconds(1.2) });
        }

        private void OnViewerSave(object s, RoutedEventArgs e) { if (_viewItem != null) SaveImage(_viewItem); }

        private void OnViewerOpen(object s, RoutedEventArgs e)
        {
            var path = _viewItem != null ? ViewPath(_viewItem) : null;
            if (path == null) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
                CloseViewer(instant: true);
                AppHost.Window.Collapse();
            }
            catch (Exception ex) { App.Log("открыть картинку: " + ex.Message); }
        }
    }
}
