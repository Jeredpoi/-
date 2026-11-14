using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Isle.Services;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using FeatureLevel = Vortice.Direct3D.FeatureLevel;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using WinRT;

namespace Isle.Helpers
{
    /// <summary>
    /// Графический слой Windows (Windows.UI.Composition) для тяжёлых экранов: тот же слой, на котором рисует
    /// анимации сама Windows. Всё — на видеокарте, прозрачность настоящая (без копирования картинки окна в память,
    /// как у «прозрачных» окон WPF), анимации ведёт DWM — ровно с частотой монитора, даже если Isle занят.
    /// Координаты — в пикселях экрана.
    /// </summary>
    public static class Comp
    {
        private static Compositor _compositor;
        private static object _queueController;
        private static CompositionGraphicsDevice _device;
        private static ID3D11Device _d3d;
        private static ID2D1Device _d2d;

        // ---------- DispatcherQueue: без него Compositor не создаётся в обычном (не UWP) потоке ----------
        [StructLayout(LayoutKind.Sequential)]
        private struct DispatcherQueueOptions { public int dwSize; public int threadType; public int apartmentType; }
        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out IntPtr controller);

        [ComImport, Guid("29E691FA-4567-4DCA-B319-D0F207EB6807"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorDesktopInterop
        {
            void CreateDesktopWindowTarget(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool isTopmost, out IntPtr target);
        }

        [ComImport, Guid("25297D5C-3AD4-4C9C-B5CF-E36A38512330"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositorInterop
        {
            void CreateCompositionSurfaceForHandle(IntPtr handle, out IntPtr surface);
            void CreateCompositionSurfaceForSwapChain(IntPtr swapChain, out IntPtr surface);
            void CreateGraphicsDevice(IntPtr renderingDevice, out IntPtr device);
        }

        [ComImport, Guid("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ICompositionDrawingSurfaceInterop
        {
            void BeginDraw(IntPtr updateRect, ref Guid iid, out IntPtr updateObject, out RawPoint updateOffset);
            void EndDraw();
            void Resize(RawSize sizePixels);
            void Scroll(IntPtr scrollRect, IntPtr clipRect, int offsetX, int offsetY);
            void ResumeDraw();
            void SuspendDraw();
        }
        [StructLayout(LayoutKind.Sequential)] private struct RawPoint { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RawSize { public int W, H; }

        private static T Interop<T>(object winrtObject) where T : class
        {
            var ptr = ((IWinRTObject)winrtObject).NativeObject.ThisPtr;
            return (T)Marshal.GetObjectForIUnknown(ptr);
        }

        /// <summary>Compositor потока интерфейса (создаётся один раз).</summary>
        public static Compositor Compositor
        {
            get
            {
                if (_compositor != null) return _compositor;
                if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
                {
                    var opt = new DispatcherQueueOptions { dwSize = Marshal.SizeOf<DispatcherQueueOptions>(), threadType = 2 /* CURRENT */, apartmentType = 2 /* STA */ };
                    Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(opt, out var ctrl));
                    _queueController = ctrl; // держим ссылку: очередь живёт, пока жив контроллер
                }
                _compositor = new Compositor();
                Journal.Info("comp", "графический слой Windows готов");
                return _compositor;
            }
        }

        /// <summary>Привязать дерево визуалов к окну (поверх его собственного содержимого или под ним).</summary>
        public static DesktopWindowTarget Target(IntPtr hwnd, bool topmost = true)
        {
            Interop<ICompositorDesktopInterop>(Compositor).CreateDesktopWindowTarget(hwnd, topmost, out var raw);
            var target = DesktopWindowTarget.FromAbi(raw);
            Marshal.Release(raw);
            return target;
        }

        /// <summary>Устройство для картинок: Direct3D 11 → Direct2D → графическое устройство слоя.</summary>
        public static CompositionGraphicsDevice Device
        {
            get
            {
                if (_device != null) return _device;
                var levels = new[] { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0 };
                var hr = D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Hardware, DeviceCreationFlags.BgraSupport, levels, out _d3d);
                if (hr.Failure)
                {
                    Journal.Warn("comp", "видеокарта не дала устройство (" + hr + ") — беру программное WARP");
                    D3D11.D3D11CreateDevice(IntPtr.Zero, DriverType.Warp, DeviceCreationFlags.BgraSupport, levels, out _d3d).CheckError();
                }
                using (var dxgi = _d3d.QueryInterface<IDXGIDevice>())
                using (var factory = D2D1.D2D1CreateFactory<ID2D1Factory1>())
                    _d2d = factory.CreateDevice(dxgi);
                Interop<ICompositorInterop>(Compositor).CreateGraphicsDevice(_d2d.NativePointer, out var raw);
                _device = CompositionGraphicsDevice.FromAbi(raw);
                Marshal.Release(raw);
                Journal.Info("comp", "устройство картинок: Direct3D " + _d3d.FeatureLevel);
                return _device;
            }
        }

        /// <summary>Картинка WPF → поверхность слоя (копируется на видеокарту один раз).</summary>
        public static CompositionDrawingSurface Surface(BitmapSource bmp)
        {
            if (bmp.Format != PixelFormats.Pbgra32)
            {
                var c = new FormatConvertedBitmap(bmp, PixelFormats.Pbgra32, null, 0);
                c.Freeze();
                bmp = c;
            }
            int w = bmp.PixelWidth, h = bmp.PixelHeight, stride = w * 4;
            // пиксели — во временный буфер вне кучи .NET: 4K-снимок не оставляет за собой 33 МБ мусора
            var buf = Marshal.AllocHGlobal(stride * h);
            try
            {
                bmp.CopyPixels(new System.Windows.Int32Rect(0, 0, w, h), buf, stride * h, stride);
                var surface = Device.CreateDrawingSurface(new Windows.Foundation.Size(w, h),
                    Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized, Windows.Graphics.DirectX.DirectXAlphaMode.Premultiplied);
                Draw(surface, buf, w, h, stride);
                return surface;
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        /// <summary>Записать пиксели (BGRA, с умноженной альфой) в поверхность.</summary>
        public static void Draw(CompositionDrawingSurface surface, byte[] px, int w, int h, int stride)
        {
            var handle = GCHandle.Alloc(px, GCHandleType.Pinned);
            try { Draw(surface, handle.AddrOfPinnedObject(), w, h, stride); }
            finally { handle.Free(); }
        }

        public static void Draw(CompositionDrawingSurface surface, IntPtr px, int w, int h, int stride)
        {
            var interop = Interop<ICompositionDrawingSurfaceInterop>(surface);
            var iid = typeof(ID2D1DeviceContext).GUID;
            interop.BeginDraw(IntPtr.Zero, ref iid, out var ctxPtr, out var offset);
            try
            {
                using var ctx = new ID2D1DeviceContext(ctxPtr);
                var props = new BitmapProperties(new Vortice.DCommon.PixelFormat(Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied));
                {
                    using var bitmap = ctx.CreateBitmap(new Vortice.Mathematics.SizeI(w, h), px, (uint)stride, props);
                    ctx.PrimitiveBlend = PrimitiveBlend.Copy; // заменить пиксели, а не смешать с тем, что было
                    ctx.DrawBitmap(bitmap, new Vortice.Mathematics.Rect(offset.X, offset.Y, w, h), 1f, BitmapInterpolationMode.NearestNeighbor, null);
                }
            }
            finally { interop.EndDraw(); }
        }
    }
}
