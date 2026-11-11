using System;
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
            if (await Capture(CaptureMode.Region) is BitmapSource bmp) Done(bmp);
        }

        public static async Task FullShot()
        {
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

        private static void Done(BitmapSource bmp)
        {
            ScreenTools.CopyImage(bmp);
            string path = null;
            if (AppSettings.Current.SaveScreenshots) { try { path = ScreenTools.SavePng(bmp); } catch { } }
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = "Снимок скопирован",
                Subtitle = path != null ? "Сохранён в Изображения\\Isle" : "В буфере обмена",
                Image = bmp, Icon = Icons.Crop, Important = true, Duration = TimeSpan.FromSeconds(4.5),
                FilePath = path, EditImage = bmp
            });
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
            ClipboardService.SetText(text);
            ToastBus.Show(new Toast
            {
                Kind = ToastKind.Success, Title = "Текст распознан",
                Subtitle = "Скопировано · " + text.Length + " симв. · " + text.Replace("\r", " ").Replace("\n", " "), Icon = Icons.Scan, Accent = Green, Important = true,
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
                Subtitle = text, Icon = Icons.Qr, Accent = Green, Important = true, Duration = TimeSpan.FromSeconds(4)
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

        /// <summary>Запись экрана: если запущен OBS — старт/стоп записи в нём, иначе «Ножницы» Windows 11.</summary>
        public static async void Record()
        {
            AppHost.Window.Collapse();
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
                }
                catch (Exception ex)
                {
                    ToastBus.Show(new Toast
                    {
                        Kind = ToastKind.Error, Title = "OBS не отвечает", Subtitle = "Включи в OBS: Инструменты → Настройки сервера WebSocket. " + ex.Message,
                        Icon = Icons.Record, Important = true, Duration = TimeSpan.FromSeconds(6)
                    });
                }
                return;
            }
            // OBS установлен, но закрыт — запускаем его свёрнутым и сразу начинаем запись
            if (AppLauncher.Find("obs") is string obs && obs.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                AppLauncher.Launch(obs, "--startrecording --minimize-to-tray --disable-shutdown-check");
                ToastBus.Show(new Toast
                {
                    Title = "Запускаю OBS и начинаю запись", Subtitle = "Нажми «Запись экрана» ещё раз, чтобы остановить",
                    Icon = Icons.Record, Accent = Color.FromRgb(0xF8, 0x71, 0x71), Important = true, Duration = TimeSpan.FromSeconds(4)
                });
                return;
            }
            ToastBus.Show(new Toast { Title = "OBS не установлен — открываю «Ножницы»", Subtitle = "Поставь OBS Studio, и кнопка будет писать через него", Icon = Icons.Record, Important = true, Duration = TimeSpan.FromSeconds(3) });
            await Task.Delay(300);
            Win32.SendKeyCombo(Win32.VK_LWIN, 0x10 /*Shift*/, 0x52 /*R*/);
        }
    }
}
