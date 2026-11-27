using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Native;
using Isle.Overlays;
using Isle.Services;

namespace Isle
{
    /// <summary>Действия экранных инструментов с ответом в виде всплывашки.</summary>
    public static class Tools
    {
        private static readonly Color Green = Color.FromRgb(0x4A, 0xDE, 0x80);

        private static bool _capturing;

        private static async Task<object> Capture(CaptureMode mode)
        {
            if (_capturing) return null; // повторное нажатие PrintScreen во время выделения — не открываем второй слой
            _capturing = true;
            try
            {
                // «Показывать остров на скриншотах»: снимаем экран вместе со свёрнутой чёлкой, и только потом прячем её под слоем выделения
                BitmapSource shot = null;
                if (AppSettings.Current.ShowInCaptures && mode == CaptureMode.Region)
                {
                    shot = await ShotWithIslandAsync();
                    App.Trace("снимок: остров попадёт в снимок (настройка «видно на скриншотах»)");
                }
                await HideIslandAsync();
                try { return await CaptureOverlay.Run(mode, shot); }
                finally { AppHost.Window.Visibility = Visibility.Visible; }
            }
            finally { _capturing = false; }
        }

        /// <summary>
        /// Убрать чёлку с экрана перед снимком. Раскрытая панель (кнопки «Текст», «QR» на Главной) успевала попасть
        /// в «замороженный» снимок под выделением и закрывала то, что под ней, — поэтому ждём, пока Windows
        /// действительно перерисует экран без неё (DwmFlush — до конца кадра).
        /// </summary>
        private static async Task HideIslandAsync()
        {
            var w = AppHost.Window;
            bool wasOpen = w.State != IslandState.Collapsed;
            w.Collapse();
            w.Visibility = Visibility.Hidden;
            await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            if (wasOpen || AppSettings.Current.ShowInCaptures) await Task.Delay(200);
            try { Win32.DwmFlush(); Win32.DwmFlush(); } catch { }
        }

        public static async Task RegionShot()
        {
            var r = await Capture(CaptureMode.Region);
            if (r is BitmapSource bmp) Done(bmp);
            else if (r is VideoRegion v) await StartOwnRecording(v);
        }

        public static async Task FullShot()
        {
            ClipboardService.NoteShotApp(); // весь экран тоже относится к приложению, которое было впереди
            BitmapSource bmp;
            if (AppSettings.Current.ShowInCaptures) { bmp = await ShotWithIslandAsync(); Done(bmp); return; }
            await HideIslandAsync();
            try { bmp = ScreenTools.CaptureAll(); }
            finally { AppHost.Window.Visibility = Visibility.Visible; }
            Done(bmp);
        }

        /// <summary>Снимок всего экрана со свёрнутой чёлкой: сворачиваем, ждём конец анимации и кадр DWM, снимаем.</summary>
        private static async Task<BitmapSource> ShotWithIslandAsync()
        {
            var w = AppHost.Window;
            bool wasOpen = w.State != IslandState.Collapsed;
            w.Collapse();
            await w.Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);
            await Task.Delay(wasOpen ? 380 : 60); // сворачивание — пружина ~0,35 с
            try { Win32.DwmFlush(); Win32.DwmFlush(); } catch { }
            return ScreenTools.CaptureAll();
        }

        /// <summary>Уменьшенная копия (не держит исходную картинку).</summary>
        public static BitmapSource Thumb(BitmapSource bmp, int maxSide = 360)
        {
            try
            {
                double k = Math.Min(1, maxSide / (double)Math.Max(bmp.PixelWidth, bmp.PixelHeight));
                if (k >= 1) return bmp;
                var wb = new System.Windows.Media.Imaging.WriteableBitmap(new System.Windows.Media.Imaging.TransformedBitmap(bmp, new System.Windows.Media.ScaleTransform(k, k)));
                wb.Freeze();
                return wb;
            }
            catch { return bmp; }
        }

        private static bool _shotLate;

        private static void Done(BitmapSource bmp)
        {
            ClipboardService.ShotTaken(bmp);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool copied = ScreenTools.CopyImage(bmp, ok =>
            {
                // буфер был занят и освободился позже — сообщаем итог отдельно
                if (_shotLate) ToastBus.Show(new Toast { Kind = ok ? ToastKind.Success : ToastKind.Error, Title = ok ? "Снимок скопирован" : "Снимок не скопировался",
                    Subtitle = ok ? "Буфер обмена освободился" : "Буфер так и не освободился — снимок в Изображения\\Isle", Icon = Icons.Crop, Important = true, Duration = TimeSpan.FromSeconds(2.5) });
                _shotLate = false;
            });
            _shotLate = !copied;
            long copyMs = sw.ElapsedMilliseconds;
            string path = null;
            if (AppSettings.Current.SaveScreenshots)
            {
                // PNG 4K сжимается долго — в фоне, чтобы экран не замирал (картинка заморожена, из другого потока читать можно)
                path = ScreenTools.NewShotPath();
                var target = path;
                if (!bmp.IsFrozen) bmp.Freeze();
                _ = Task.Run(() =>
                {
                    var s2 = System.Diagnostics.Stopwatch.StartNew();
                    try { ScreenTools.SavePng(bmp, target); Journal.Info("capture", "PNG сохранён за " + s2.ElapsedMilliseconds + " мс: " + System.IO.Path.GetFileName(target)); }
                    catch (Exception ex) { Journal.Error("capture", "PNG не сохранился: " + ex.Message); }
                });
            }
            Journal.Info("capture", "снимок " + bmp.PixelWidth + "×" + bmp.PixelHeight + " в буфере за " + copyMs + " мс");
            ToastBus.Show(new Toast
            {
                Kind = copied ? ToastKind.Success : ToastKind.Info, Title = copied ? "Снимок скопирован" : "Буфер обмена занят",
                Subtitle = !copied ? "Скопирую, как освободится" + (path != null ? " · сохранён в Изображения\\Isle" : "") : path != null ? "Сохранён в Изображения\\Isle" : "В буфере обмена",
                // в уведомлении — маленькая копия, а для «редактировать» снимок читается из файла: полноразмерный
                // 4K-снимок (десятки мегабайт) больше не висит в памяти, пока не придёт следующее уведомление
                Image = Thumb(bmp), Icon = Icons.Crop, Important = true, Duration = TimeSpan.FromSeconds(4.5),
                FilePath = path, EditImage = path == null ? bmp : null, EditFromFile = path != null
            });
            Helpers.MemoryTrim.Soon("снимок");
        }

        public static async Task OcrShot()
        {
            if (await Capture(CaptureMode.Ocr) is not BitmapSource bmp) return;
            string text = null;
            try { text = await ScreenTools.OcrAsync(bmp); }
            catch (Exception ex) { App.Log("ocr: " + ex); }
            if (string.IsNullOrWhiteSpace(text))
            {
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Error, Title = "Текст не найден", Subtitle = "Проверь, что в Windows установлен язык с распознаванием текста",
                    Icon = Icons.Scan, Important = true, Duration = TimeSpan.FromSeconds(4)
                });
                return;
            }
            bool late = false;
            bool copied = ClipboardService.SetText(text, ok =>
            {
                if (late) ToastBus.Show(new Toast { Kind = ok ? ToastKind.Success : ToastKind.Error, Title = ok ? "Текст скопирован" : "Текст не скопировался",
                    Subtitle = ok ? "Буфер обмена освободился" : "Буфер так и не освободился — попробуй ещё раз", Icon = Icons.Scan, Important = true, Duration = TimeSpan.FromSeconds(2.5) });
            });
            late = !copied;
            ToastBus.Show(new Toast
            {
                Kind = copied ? ToastKind.Success : ToastKind.Info, Title = copied ? "Текст распознан" : "Текст распознан, буфер занят", Private = true,
                Subtitle = (copied ? "Скопировано · " : "Скопирую, как освободится · ") + text.Length + " симв. · " + text.Replace("\r", " ").Replace("\n", " "), Icon = Icons.Scan, Accent = Green, Important = true,
                Duration = TimeSpan.FromSeconds(3.5)
            });
        }

        public static async Task QrShot()
        {
            if (await Capture(CaptureMode.Qr) is not BitmapSource bmp) return;
            var text = ScreenTools.ReadQr(bmp);
            if (string.IsNullOrEmpty(text))
            {
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "QR-код не найден", Subtitle = "Выдели код целиком, с небольшим полем", Icon = Icons.Qr, Important = true, Duration = TimeSpan.FromSeconds(3) });
                return;
            }
            ClipboardService.SetText(text);
            bool url = Uri.TryCreate(text, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = url ? "Ссылка из QR скопирована" : "QR прочитан и скопирован",
                Subtitle = text, Private = true, Icon = Icons.Qr, Accent = Green, Important = true, Duration = TimeSpan.FromSeconds(4)
            });
        }

        public static async Task Pipette()
        {
            if (await Capture(CaptureMode.Pipette) is not Color c) return;
            var hex = CaptureOverlay.Hex(c);
            ClipboardService.SetText(hex);
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = hex, Subtitle = "rgb(" + c.R + ", " + c.G + ", " + c.B + ")",
                Icon = Icons.Pipette, Accent = c, Swatch = c, Important = true, Duration = TimeSpan.FromSeconds(3.5)
            });
        }

        /// <summary>Убирает форматирование из буфера (оставляет только текст).</summary>
        public static void CleanText()
        {
            string text = null;
            bool image = false;
            try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); else image = Clipboard.ContainsImage(); } catch { }
            if (string.IsNullOrEmpty(text))
            {
                ToastBus.Show(new Toast
                {
                    Kind = ToastKind.Error, Title = image ? "В буфере картинка" : "В буфере нет текста",
                    Subtitle = "«Чистый текст» убирает шрифты и цвета у скопированного текста — скопируй текст и нажми снова",
                    Icon = Icons.Eraser, Important = true, Duration = TimeSpan.FromSeconds(4)
                });
                return;
            }
            ClipboardService.SetText(text);
            ToastBus.Show(new Toast { Kind = ToastKind.Success, Title = "Форматирование убрано", Subtitle = text.Length + " симв. — вставляй", Icon = Icons.Eraser, Accent = Green, Important = true });
        }

        private static RecordFrame _recFrame;

        /// <summary>
        /// Запись экрана: выделить область → запись в MP4, повторное нажатие — стоп.
        /// OBS — основной: если он запущен, старт/стоп записи в нём; иначе Isle пишет выбранную область сам.
        /// </summary>
        public static async void Record()
        {
            AppHost.Window.Collapse();
            if (RegionRecorder.Current != null) { await StopOwnRecording(); return; }
            // OBS — основной: запущен — пишем в нём. Не запущен (или не отвечает) — пишем сами выбранную область
            if (ObsService.IsObsRunning())
            {
                try
                {
                    var (on, file) = await ObsService.ToggleRecordAsync();
                    if (on)
                        ToastBus.Show(new Toast
                        {
                            Title = "OBS: запись идёт", Subtitle = "Нажми «Запись экрана» ещё раз, чтобы остановить",
                            Icon = Icons.Record, Accent = Color.FromRgb(0xF8, 0x71, 0x71), Important = true, Duration = TimeSpan.FromSeconds(3)
                        });
                    else ObsService.ShowSaved(file); // с кнопкой «папка» — откроет Проводник с выделенным видео
                    return;
                }
                catch (Exception ex)
                {
                    Journal.Warn("record", "OBS не ответил, пишу сам: " + ex.Message);
                    ToastBus.Show(new Toast
                    {
                        Warning = true, Title = "OBS не отвечает — пишу сам", Subtitle = "Чтобы писать в OBS: Инструменты → Настройки сервера WebSocket",
                        Icon = Icons.Record, Important = true, Duration = TimeSpan.FromSeconds(4)
                    });
                }
            }
            await StartOwnRecording();
        }

        private static async Task StartOwnRecording(VideoRegion preset = null)
        {
            if (RegionRecorder.Current != null) return;
            await Task.Delay(200); // чёлка успевает свернуться — не мешает выделению
            var picker = preset != null ? new RecordPicker(preset.X, preset.Y, preset.W, preset.H) : new RecordPicker();
            picker.ShowDialog();
            if (picker.Result is not { } r) return;
            _recFrame = new RecordFrame(r.x, r.y, r.w, r.h);
            _recFrame.Show();
            try
            {
                await RegionRecorder.StartAsync(r.x, r.y, r.w, r.h, r.sys, r.mic);
                ToastBus.Show(new Toast { Title = "Запись идёт", Subtitle = "Остановить — клик по REC в чёлке или " + (AppSettings.Current.HotkeyRecord ?? "Ctrl+Alt+R"),
                    Icon = Icons.Record, Accent = Color.FromRgb(0xF8, 0x71, 0x71), Important = true, Duration = TimeSpan.FromSeconds(2.5) });
            }
            catch (Exception ex)
            {
                _recFrame?.Close(); _recFrame = null;
                Journal.Error("record", ex);
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Запись не началась", Subtitle = ex.Message, Icon = Icons.Record, Important = true, Duration = TimeSpan.FromSeconds(6) });
            }
        }

        private static async Task StopOwnRecording()
        {
            _recFrame?.Close(); _recFrame = null;
            try
            {
                var file = await RegionRecorder.StopAsync();
                if (file == null) return;
                long size = new FileInfo(file).Length;
                Journal.Info("record", "сохранено " + (size >> 20) + " МБ");
                var t = ObsService.SavedToast(file);
                t.Subtitle = Path.GetFileName(file) + " · " + (size > 1 << 20 ? (size / 1048576.0).ToString("0.0") + " МБ" : (size >> 10) + " КБ");
                t.ReplaceKey = "rec-saved";
                ToastBus.Show(t);
            }
            catch (Exception ex)
            {
                Journal.Error("record", ex);
                ToastBus.Show(new Toast { Kind = ToastKind.Error, Title = "Видео не сохранилось", Subtitle = ex.Message, Icon = Icons.Record, Important = true, Duration = TimeSpan.FromSeconds(6) });
            }
        }
    }
}
