using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace CamWatch;

/// <summary>摄像头采集：枚举设备、取流、转成 Bitmap、简易移动侦测。</summary>
sealed class CameraCapture : IDisposable
{
    MediaCapture? _mediaCapture;
    MediaFrameReader? _frameReader;
    int _running;
    long _nextTicks;

    // 移动区域标注用的低分辨率灰度采样
    const int SampleW = 64, SampleH = 48;
    readonly byte[] _prevGray = new byte[SampleW * SampleH];
    readonly byte[] _curGray = new byte[SampleW * SampleH];
    readonly byte[] _motionGrid = new byte[SampleW * SampleH];
    readonly List<Rectangle> _motionBoxes = new();
    bool _hasPrev;

    /// <summary>是否在画面上标注移动红框（右键菜单开关）。</summary>
    public volatile bool MotionBoxesEnabled = true;

    public event Action<Bitmap>? FrameArrived;
    public int FramesReceived;
    public int FrameEvents;
    public int NullBitmaps;
    public int FrameErrors;
    public string LastSourceInfo = "";
    public string LastError = "";

    public static async Task<List<(string Name, string Id)>> ListCamerasAsync()
    {
        var list = new List<(string, string)>();
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
        foreach (var d in devices)
            list.Add((d.Name ?? "未知设备", d.Id));
        return list;
    }

    public async Task StartAsync(string? deviceId)
    {
        await StopAsync();

        var settings = new MediaCaptureInitializationSettings
        {
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
        };
        if (!string.IsNullOrEmpty(deviceId))
            settings.VideoDeviceId = deviceId;

        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(settings);
        }
        catch
        {
            capture.Dispose();
            throw;
        }

        MediaFrameSource? source = null;
        foreach (var kv in capture.FrameSources)
        {
            var t = kv.Value.Info.MediaStreamType;
            if (t == MediaStreamType.VideoRecord) { source = kv.Value; break; }
            if (t == MediaStreamType.VideoPreview && source == null) source = kv.Value;
        }
        source ??= capture.FrameSources.Values.FirstOrDefault();

        if (source == null)
        {
            capture.Dispose();
            throw new InvalidOperationException("未找到可用的视频帧源");
        }

        // 优先请求 BGRA8 输出（部分驱动可借此转出 CPU 位图）；不支持则用默认
        MediaFrameReader reader;
        try
        {
            reader = await capture.CreateFrameReaderAsync(source, Windows.Media.MediaProperties.MediaEncodingSubtypes.Bgra8);
        }
        catch
        {
            reader = await capture.CreateFrameReaderAsync(source);
        }
        reader.FrameArrived += OnFrameArrived;

        _mediaCapture = capture;
        _frameReader = reader;
        _running = 1;
        _hasPrev = false;
        LastSourceInfo = $"{source.Info.Id} kind={source.Info.SourceKind} type={source.Info.MediaStreamType}";
        Interlocked.Exchange(ref FramesReceived, 0);
        Interlocked.Exchange(ref FrameEvents, 0);
        Interlocked.Exchange(ref NullBitmaps, 0);
        Interlocked.Exchange(ref FrameErrors, 0);

        var status = await reader.StartAsync();
        if (status != MediaFrameReaderStartStatus.Success)
        {
            await StopAsync();
            throw new InvalidOperationException("摄像头取流启动失败: " + status);
        }
    }

    public async Task StopAsync()
    {
        Interlocked.Exchange(ref _running, 0);
        var reader = Interlocked.Exchange(ref _frameReader, null);
        var capture = Interlocked.Exchange(ref _mediaCapture, null);
        if (reader != null)
        {
            try { reader.FrameArrived -= OnFrameArrived; await reader.StopAsync(); } catch { }
            reader.Dispose();
        }
        if (capture != null)
        {
            try { capture.Dispose(); } catch { }
        }
        _hasPrev = false;
    }

    void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        if (Volatile.Read(ref _running) == 0) return;

        // 限帧，控资源占用（约 15 帧/秒）
        var now = Environment.TickCount64;
        if (now < _nextTicks) return;
        _nextTicks = now + 66;

        using var frameRef = sender.TryAcquireLatestFrame();
        Interlocked.Increment(ref FrameEvents);
        var videoFrame = frameRef?.VideoMediaFrame;
        if (videoFrame == null)
        {
            Interlocked.Increment(ref NullBitmaps);
            return;
        }

        SoftwareBitmap? sb = videoFrame.SoftwareBitmap;

        // 部分驱动只给 D3D 表面，SoftwareBitmap 为空，回退用表面拷贝
        if (sb == null && videoFrame.Direct3DSurface != null)
        {
            try
            {
                sb = SoftwareBitmap.CreateCopyFromSurfaceAsync(videoFrame.Direct3DSurface)
                    .AsTask().GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref FrameErrors);
                LastError = "surface-copy: " + ex.Message;
                return;
            }
        }

        if (sb == null)
        {
            Interlocked.Increment(ref NullBitmaps);
            return;
        }

        try
        {
            var bmp = SoftwareBitmapToBitmap(sb);
            if (bmp == null) return;
            Interlocked.Increment(ref FramesReceived);
            FrameArrived?.Invoke(bmp);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref FrameErrors);
            LastError = ex.ToString();
        }
        finally
        {
            if (!ReferenceEquals(sb, videoFrame.SoftwareBitmap))
                sb.Dispose();
        }
    }

    /// <summary>返回 null 表示本帧未转换成功。</summary>
    unsafe Bitmap? SoftwareBitmapToBitmap(SoftwareBitmap sb)
    {
        SoftwareBitmap? converted = null;
        var src = sb;
        if (src.BitmapPixelFormat != BitmapPixelFormat.Bgra8)
        {
            converted = SoftwareBitmap.Convert(src, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            src = converted;
        }

        try
        {
            using var buffer = src.LockBuffer(BitmapBufferAccessMode.Read);
            using var reference = buffer.CreateReference();

            // 首选零拷贝：拿内存指针（reference 或 buffer 对象实现 IMemoryBufferByteAccess）
            if (BufferAccess.TryGetRawBuffer(reference, out byte* ptr, out _) ||
                BufferAccess.TryGetRawBuffer(buffer, out ptr, out _))
            {
                var desc = buffer.GetPlaneDescription(0);
                int w = desc.Width, h = desc.Height;
                if (w <= 0 || h <= 0) return null;

                byte* basePtr = ptr + desc.StartIndex;

                // 移动侦测：抽样灰度差分（48x32 网格）
                DetectMotion(basePtr, desc.Stride, w, h);

                var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    int dstStride = bd.Stride;
                    byte* dst = (byte*)bd.Scan0;
                    if (dstStride == desc.Stride)
                    {
                        Buffer.MemoryCopy(basePtr, dst, (long)dstStride * h, (long)desc.Stride * h);
                    }
                    else
                    {
                        for (int y = 0; y < h; y++)
                            Buffer.MemoryCopy(basePtr + (long)y * desc.Stride, dst + (long)y * dstStride, dstStride, desc.Stride);
                    }
                }
            finally
            {
                bmp.UnlockBits(bd);
            }
            DrawMotionBoxes(bmp);
            return bmp;
            }

            // 回退：经 BMP 编码器转出（无需内存指针，兼容所有驱动）
            return EncodeToBitmap(src);
        }
        finally
        {
            converted?.Dispose();
        }
    }

    Bitmap EncodeToBitmap(SoftwareBitmap src)
    {
        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        try
        {
            Task.Run(async () =>
            {
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                    Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId, stream);
                encoder.SetSoftwareBitmap(src);
                await encoder.FlushAsync();
            }).GetAwaiter().GetResult();

            stream.Seek(0);
            using var managed = stream.AsStreamForRead();
            using var decoded = new Bitmap(managed);

            // 回退路径上也做移动区域检测（仅 32bpp 位图）
            if (Image.GetPixelFormatSize(decoded.PixelFormat) == 32)
            {
                var bd = decoded.LockBits(new Rectangle(0, 0, decoded.Width, decoded.Height),
                    ImageLockMode.ReadOnly, decoded.PixelFormat);
                unsafe
                {
                    DetectMotion((byte*)bd.Scan0, bd.Stride, decoded.Width, decoded.Height);
                }
                decoded.UnlockBits(bd);
            }
            else
            {
                _motionBoxes.Clear();
            }

            var copy = new Bitmap(decoded); // 复制一份，允许流释放
            DrawMotionBoxes(copy);
            return copy;
        }
        finally
        {
            stream.Dispose();
        }
    }

    /// <summary>逐帧差分找出移动区域，结果放 _motionBoxes（图像坐标）。</summary>
    unsafe void DetectMotion(byte* basePtr, int stride, int w, int h)
    {
        _motionBoxes.Clear();

        // 开关关闭时不做检测（重新开启后先存一帧基准，避免整屏误报）
        if (!MotionBoxesEnabled)
        {
            _hasPrev = false;
            return;
        }

        for (int sy = 0; sy < SampleH; sy++)
        {
            int y = sy * h / SampleH;
            byte* row = basePtr + (long)y * stride;
            for (int sx = 0; sx < SampleW; sx++)
            {
                int x = sx * w / SampleW;
                byte* p = row + (long)x * 4;
                _curGray[sy * SampleW + sx] = (byte)((p[0] * 29 + p[1] * 150 + p[2] * 77) >> 8);
            }
        }

        if (_hasPrev)
        {
            int motionCount = 0;
            for (int i = 0; i < _curGray.Length; i++)
            {
                bool move = Math.Abs(_curGray[i] - _prevGray[i]) >= 18;
                _motionGrid[i] = move ? (byte)1 : (byte)0;
                if (move) motionCount++;
            }

            // 连通区域 -> 包围盒，标注成小红框
            if (motionCount >= 3)
            {
                var stack = new Stack<int>();
                for (int i = 0; i < _motionGrid.Length; i++)
                {
                    if (_motionGrid[i] == 0) continue;
                    int minX = SampleW, minY = SampleH, maxX = 0, maxY = 0, count = 0;
                    stack.Push(i);
                    _motionGrid[i] = 0;
                    while (stack.Count > 0)
                    {
                        int c = stack.Pop();
                        int cx = c % SampleW, cy = c / SampleW;
                        count++;
                        if (cx < minX) minX = cx;
                        if (cx > maxX) maxX = cx;
                        if (cy < minY) minY = cy;
                        if (cy > maxY) maxY = cy;

                        // 8 邻域：斜向相邻的碎片也视为同一块
                        for (int dy = -1; dy <= 1; dy++)
                        {
                            int ny = cy + dy;
                            if (ny < 0 || ny >= SampleH) continue;
                            for (int dx = -1; dx <= 1; dx++)
                            {
                                if (dx == 0 && dy == 0) continue;
                                int nx = cx + dx;
                                if (nx < 0 || nx >= SampleW) continue;
                                int nc = ny * SampleW + nx;
                                if (_motionGrid[nc] == 1) { _motionGrid[nc] = 0; stack.Push(nc); }
                            }
                        }
                    }

                    if (count >= 2 && _motionBoxes.Count < 10)
                    {
                        int x0 = minX * w / SampleW;
                        int y0 = minY * h / SampleH;
                        int x1 = (maxX + 1) * w / SampleW;
                        int y1 = (maxY + 1) * h / SampleH;
                        var box = Rectangle.Inflate(new Rectangle(x0, y0, Math.Max(2, x1 - x0), Math.Max(2, y1 - y0)), 4, 4);
                        _motionBoxes.Add(Rectangle.Intersect(box, new Rectangle(0, 0, w, h)));
                    }
                }
            }

            // 同一移动物常断成多块（四肢/躯干各一块），按间距合并成大框
            MergeNearbyBoxes(w);
        }

        Buffer.BlockCopy(_curGray, 0, _prevGray, 0, _curGray.Length);
        _hasPrev = true;
    }

    /// <summary>把相互靠近的小框合并成大框：人在走动时身上的多个碎框 → 一个框。</summary>
    void MergeNearbyBoxes(int frameWidth)
    {
        if (_motionBoxes.Count < 2) return;
        int gap = Math.Max(14, frameWidth / 12);
        bool merged = true;
        while (merged && _motionBoxes.Count > 1)
        {
            merged = false;
            for (int i = 0; i < _motionBoxes.Count && !merged; i++)
            {
                var grown = Rectangle.Inflate(_motionBoxes[i], gap, gap);
                for (int j = i + 1; j < _motionBoxes.Count; j++)
                {
                    if (grown.IntersectsWith(_motionBoxes[j]))
                    {
                        _motionBoxes[i] = Rectangle.Union(_motionBoxes[i], _motionBoxes[j]);
                        _motionBoxes.RemoveAt(j);
                        merged = true;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>把移动区域小红框画到帧上。</summary>
    void DrawMotionBoxes(Bitmap bmp)
    {
        if (_motionBoxes.Count == 0) return;
        using var g = Graphics.FromImage(bmp);
        using var pen = new Pen(Color.FromArgb(230, 30, 30), 2);
        foreach (var box in _motionBoxes)
            g.DrawRectangle(pen, box);
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();
}

/// <summary>IMemoryBufferByteAccess：WinRT 内存缓冲区取原始字节指针。</summary>
[ComImport]
[Guid("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
unsafe interface IMemoryBufferByteAccess
{
    void GetBuffer(out byte* buffer, out uint capacity);
}

static unsafe class BufferAccess
{
    /// <summary>CsWinRT 的 RCW 不能直接强转 COM 接口，这里走 IUnknown QI + vtable 直接调用。</summary>
    public static bool TryGetRawBuffer(object winrtObj, out byte* ptr, out uint capacity)
    {
        ptr = null;
        capacity = 0;
        IntPtr unk;
        try
        {
            unk = Marshal.GetIUnknownForObject(winrtObj);
        }
        catch
        {
            return false;
        }
        try
        {
            Guid iid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");
            if (Marshal.QueryInterface(unk, ref iid, out IntPtr pAcc) < 0) return false;
            try
            {
                // IMemoryBufferByteAccess vtable: QInterface/AddRef/Release 之后第 0 个方法就是 GetBuffer
                var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)Marshal.ReadIntPtr(pAcc, 3 * IntPtr.Size);
                byte* p;
                uint cap;
                if (getBuffer(pAcc, &p, &cap) < 0)
                {
                    return false;
                }
                ptr = p;
                capacity = cap;
                return true;
            }
            finally
            {
                Marshal.Release(pAcc);
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.Release(unk);
        }
    }
}
