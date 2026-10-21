using System.Text.Json;

namespace CamWatch;

sealed class AppSettings
{
    public int X { get; set; } = 200;
    public int Y { get; set; } = 120;
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 360;
    public bool TopMost { get; set; } = true;
    public bool Mirror { get; set; } = true;
    public bool MotionBoxes { get; set; } = true;
    public int Opacity { get; set; } = 100;
    public string? CameraId { get; set; }
}

/// <summary>无边框纯画面窗口：全部功能在右键菜单，拖动即可移动/缩放，窗口比例跟随画面。</summary>
sealed class MainForm : Form
{
    readonly CameraCapture _capture = new();
    readonly PictureBox _picture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    readonly Label _statusOverlay = new()
    {
        AutoSize = true,
        Padding = new Padding(8, 5, 8, 5),
        BackColor = Color.FromArgb(150, 20, 20, 20),
        ForeColor = Color.White,
        Font = new Font("Microsoft YaHei UI", 9f),
        Visible = false,
        Location = new Point(8, 8),
    };
    readonly System.Windows.Forms.Timer _statusTimer = new() { Interval = 2500 };
    readonly System.Windows.Forms.Timer _uiTimer = new() { Interval = 1000 };

    ContextMenuStrip _menu = null!;
    ToolStripMenuItem _miCameras = null!;
    ToolStripMenuItem _miTop = null!;
    ToolStripMenuItem _miMirror = null!;
    ToolStripMenuItem _miMotionBoxes = null!;
    TrackBar _opacitySlider = null!;

    AppSettings _settings = new();
    List<(string Name, string Id)> _cameras = new();
    bool _fullscreen;
    bool _closing;
    bool _aspectSnapped;
    double _aspect = 16.0 / 9.0;   // 画面宽高比，窗口跟随
    FormWindowState _savedState;
    Rectangle _savedBounds;
    int _lastFrames;

    // 右键拖拽调大小
    bool _rdragging;
    bool _rdragMoved;
    Point _rdragStart;
    Size _rdragStartSize;

    static string SettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CamWatch", "settings.json");

    public MainForm(bool smokeTest)
    {
        Text = "CamWatch";
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        MinimumSize = new Size(96, 54);
        ClientSize = new Size(640, 360);
        KeyPreview = true;

        LoadSettings();

        Controls.Add(_picture);
        Controls.Add(_statusOverlay);
        _statusOverlay.BringToFront();

        _menu = BuildContextMenu();
        _picture.ContextMenuStrip = _menu;

        _opacitySlider.Value = Math.Clamp(_settings.Opacity, _opacitySlider.Minimum, _opacitySlider.Maximum);
        Opacity = _opacitySlider.Value / 100.0;
        TopMost = _settings.TopMost;
        _capture.MotionBoxesEnabled = _settings.MotionBoxes;

        // 左键拖画面 = 移动窗口
        _picture.MouseDown += (_, e) =>
        {
            if (_closing || _fullscreen) return;
            if (e.Button == MouseButtons.Left)
            {
                _picture.Capture = true;
                _picture.Tag = e.Location;
            }
            else if (e.Button == MouseButtons.Right)
            {
                _rdragging = true;
                _rdragMoved = false;
                _rdragStart = e.Location;
                _rdragStartSize = Size;
            }
        };
        _picture.MouseMove += (_, e) =>
        {
            if (_closing || _fullscreen) return;
            if (e.Button == MouseButtons.Left && _picture.Tag is Point start)
            {
                Location = new Point(Location.X + e.X - start.X, Location.Y + e.Y - start.Y);
            }
            else if (_rdragging && e.Button == MouseButtons.Right)
            {
                int dx = e.X - _rdragStart.X;
                int dy = e.Y - _rdragStart.Y;
                if (Math.Abs(dx) + Math.Abs(dy) > 4) _rdragMoved = true;
                double scale = 1.0 + (dx + dy) / 320.0;
                SetSizeKeepAspect((int)(_rdragStartSize.Width * scale));
            }
        };
        _picture.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                _picture.Capture = false;
                _picture.Tag = null;
            }
            else if (e.Button == MouseButtons.Right)
            {
                bool moved = _rdragMoved;
                _rdragging = false;
                // 右键点击（未拖动）才弹菜单
                if (!moved) _menu.Show(_picture, e.Location);
            }
        };

        _capture.FrameArrived += OnFrame;

        _uiTimer.Tick += (_, _) =>
        {
            int now = _capture.FramesReceived;
            _fpsText = $"{Math.Max(0, now - _lastFrames)} FPS";
            _lastFrames = now;
        };
        _uiTimer.Start();
        StartBossKeyWatch();

        _statusTimer.Tick += (_, _) => _statusOverlay.Visible = false;

        Load += async (_, _) =>
        {
            await InitCamerasAsync();
            if (smokeTest)
            {
                var log = new System.Text.StringBuilder();
                log.AppendLine($"cameras={_cameras.Count}");
                for (int i = 0; i < _cameras.Count; i++)
                {
                    try
                    {
                        await _capture.StartAsync(_cameras[i].Id);
                    }
                    catch (Exception ex)
                    {
                        log.AppendLine($"cam{i} '{_cameras[i].Name}' START-FAIL {ex.Message}");
                        continue;
                    }
                    await Task.Delay(2500);
                    log.AppendLine($"cam{i} '{_cameras[i].Name}' frames={_capture.FramesReceived} ev={_capture.FrameEvents} null={_capture.NullBitmaps} err={_capture.FrameErrors} src=[{_capture.LastSourceInfo}] lastErr=[{_capture.LastError}]");
                }
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "camwatch_smoke.txt"), log.ToString());
                Environment.Exit(log.ToString().Contains("frames=") ? 0 : 2);
            }
        };
        FormClosing += (_, _) =>
        {
            // 整体兜底：关闭路径上任何异常都不能弹窗卡住窗口
            try
            {
                _closing = true;
                _uiTimer.Stop();
                _statusTimer.Stop();
                _bossKeyTimer.Stop();
                _capture.FrameArrived -= OnFrame;

                _settings.TopMost = _btnTopChecked;
                _settings.Mirror = _btnMirrorChecked;
                _settings.Opacity = _opacitySlider.Value;
                if (!_fullscreen)
                {
                    _settings.X = Bounds.X;
                    _settings.Y = Bounds.Y;
                    _settings.Width = Bounds.Width;
                    _settings.Height = Bounds.Height;
                }
                SaveSettings();

                // 后台释放摄像头，避免 UI 线程等待 WinRT 异步造成死锁
                var capture = _capture;
                _ = Task.Run(() => capture.Dispose());
            }
            catch { }
        };
    }

    // ---------- 右键菜单 ----------

    bool _btnTopChecked;
    bool _btnMirrorChecked;
    bool _btnMotionBoxesChecked;
    string _fpsText = "";

    ContextMenuStrip BuildContextMenu()
    {
        _btnTopChecked = _settings.TopMost;
        _btnMirrorChecked = _settings.Mirror;
        _btnMotionBoxesChecked = _settings.MotionBoxes;

        var menu = new ContextMenuStrip();
        _miCameras = new ToolStripMenuItem("摄像头");

        _miTop = new ToolStripMenuItem("窗口置顶") { CheckOnClick = true, Checked = _btnTopChecked };
        _miTop.Click += (_, _) =>
        {
            _btnTopChecked = _miTop.Checked;
            TopMost = _btnTopChecked;
            _settings.TopMost = _btnTopChecked;
            SaveSettings();
            ShowStatus(_btnTopChecked ? "已置顶" : "已取消置顶");
        };

        _miMirror = new ToolStripMenuItem("画面镜像") { CheckOnClick = true, Checked = _btnMirrorChecked };
        _miMirror.Click += (_, _) =>
        {
            _btnMirrorChecked = _miMirror.Checked;
            _settings.Mirror = _btnMirrorChecked;
            SaveSettings();
        };

        _miMotionBoxes = new ToolStripMenuItem("移动红框") { CheckOnClick = true, Checked = _btnMotionBoxesChecked };
        _miMotionBoxes.Click += (_, _) =>
        {
            _btnMotionBoxesChecked = _miMotionBoxes.Checked;
            _capture.MotionBoxesEnabled = _btnMotionBoxesChecked;
            _settings.MotionBoxes = _btnMotionBoxesChecked;
            SaveSettings();
            ShowStatus(_btnMotionBoxesChecked ? "移动红框已开启" : "移动红框已关闭");
        };

        _opacitySlider = new TrackBar
        {
            Minimum = 30,
            Maximum = 100,
            Value = Math.Clamp(_settings.Opacity, 30, 100),
            TickStyle = TickStyle.None,
            AutoSize = false,
            Width = 170,
            Height = 28,
        };
        _opacitySlider.ValueChanged += (_, _) =>
        {
            Opacity = _opacitySlider.Value / 100.0;
            _settings.Opacity = _opacitySlider.Value;
            ShowStatus($"透明度 {_opacitySlider.Value}%");
        };
        var miOpacity = new ToolStripMenuItem("透明度");
        miOpacity.DropDown.Items.Add(new ToolStripControlHost(_opacitySlider) { AutoSize = false, Width = 180, Height = 32 });

        menu.Items.Add(_miCameras);
        menu.Items.Add("截图 (Ctrl+S)", null, (_, _) => Snapshot());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_miTop);
        menu.Items.Add(_miMirror);
        menu.Items.Add(_miMotionBoxes);
        menu.Items.Add(miOpacity);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("全屏 (F11)", null, (_, _) => ToggleFullscreen());
        menu.Items.Add("隐藏窗口 (老板键 右Ctrl+右Alt+/?)", null, (_, _) => ToggleBossKey());
        menu.Items.Add("退出", null, (_, _) => Close());

        // 打开时刷新摄像头列表和勾选状态
        menu.Opening += (_, _) =>
        {
            _miTop.Checked = _btnTopChecked;
            _miMirror.Checked = _btnMirrorChecked;
            _miMotionBoxes.Checked = _btnMotionBoxesChecked;
            _miCameras.DropDownItems.Clear();
            if (_cameras.Count == 0)
            {
                _miCameras.DropDownItems.Add("（未检测到摄像头）").Enabled = false;
            }
            else
            {
                int idx = _cameraComboIndex;
                for (int i = 0; i < _cameras.Count; i++)
                {
                    int ci = i;
                    var item = new ToolStripMenuItem(_cameras[i].Name) { Checked = i == idx };
                    item.Click += async (_, _) => await SwitchCameraAsync(ci);
                    _miCameras.DropDownItems.Add(item);
                }
            }
        };
        return menu;
    }

    int _cameraComboIndex;

    void ShowStatus(string text, bool persist = false)
    {
        try
        {
            if (IsDisposed) return;
            void Show()
            {
                _statusOverlay.Text = persist ? text : $"{text}   {_fpsText}";
                _statusOverlay.Visible = true;
                _statusTimer.Stop();
                if (!persist) _statusTimer.Start();
            }
            if (InvokeRequired) BeginInvoke(Show);
            else Show();
        }
        catch { }
    }

    // ---------- 摄像头 ----------

    async Task InitCamerasAsync()
    {
        try
        {
            _cameras = await CameraCapture.ListCamerasAsync();
        }
        catch (Exception ex)
        {
            ShowStatus("无法枚举摄像头: " + ex.Message, persist: true);
            return;
        }

        if (_cameras.Count == 0)
        {
            ShowStatus("没有检测到摄像头", persist: true);
            return;
        }

        int idx = 0;
        if (!string.IsNullOrEmpty(_settings.CameraId))
        {
            int found = _cameras.FindIndex(c => c.Id == _settings.CameraId);
            if (found >= 0) idx = found;
        }

        // 默认摄像头若不输出画面，自动换到下一个能出画面的
        for (int i = 0; i < _cameras.Count && !_closing; i++)
        {
            int tryIdx = (idx + i) % _cameras.Count;
            _cameraComboIndex = tryIdx;
            await StartCameraAsync(_cameras[tryIdx].Id);
            if (_closing) return;
            await Task.Delay(1200);
            if (_capture.FramesReceived > 0)
            {
                _settings.CameraId = _cameras[tryIdx].Id;
                SaveSettings();
                return;
            }
            if (_cameras.Count == 1) break;
        }
        ShowStatus("摄像头没有画面输出（虚拟摄像头需先连接手机/应用）", persist: true);
    }

    async Task SwitchCameraAsync(int idx)
    {
        if (idx < 0 || idx >= _cameras.Count) return;
        _cameraComboIndex = idx;
        _settings.CameraId = _cameras[idx].Id;
        SaveSettings();
        await StartCameraAsync(_cameras[idx].Id);
    }

    async Task StartCameraAsync(string deviceId)
    {
        if (_closing) return;
        ShowStatus("正在启动摄像头…");
        try
        {
            _aspectSnapped = false;
            await _capture.StartAsync(deviceId);
            if (_closing)
            {
                var capture = _capture;
                _ = Task.Run(() => capture.StopAsync());
                return;
            }
            _lastFrames = _capture.FramesReceived;
            ShowStatus("就绪 — 老板键 右Ctrl+右Alt+/?");
        }
        catch (UnauthorizedAccessException)
        {
            ShowStatus("没有摄像头权限：Windows 设置 > 隐私和安全性 > 相机", persist: true);
        }
        catch (Exception ex)
        {
            ShowStatus("摄像头启动失败: " + ex.Message + "（可能被其他程序占用）", persist: true);
        }
    }

    // ---------- 显示 ----------

    void OnFrame(Bitmap bmp)
    {
        if (IsDisposed) { bmp.Dispose(); return; }
        try
        {
            BeginInvoke(() =>
            {
                try
                {
                    if (_btnMirrorChecked)
                        bmp.RotateFlip(RotateFlipType.RotateNoneFlipX);

                    // 画面分辨率变化时，把窗口调整为同比例，保证无黑边
                    if (!_fullscreen && bmp.Width > 0 && bmp.Height > 0)
                    {
                        double aspect = (double)bmp.Width / bmp.Height;
                        if (!_aspectSnapped || Math.Abs(aspect - _aspect) > 0.01)
                        {
                            _aspect = aspect;
                            _aspectSnapped = true;
                            int targetH = Math.Max(MinimumSize.Height, (int)Math.Round(Width / _aspect));
                            if (Math.Abs(targetH - Height) > 2)
                                Height = targetH;
                        }
                    }

                    var old = _picture.Image;
                    _picture.Image = bmp;
                    old?.Dispose();
                }
                catch { bmp.Dispose(); }
            });
        }
        catch
        {
            bmp.Dispose();
        }
    }

    // ---------- 截图 ----------

    void Snapshot()
    {
        var img = _picture.Image;
        if (img == null)
        {
            ShowStatus("没有画面可截图");
            return;
        }
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CamWatch");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");

            using var clone = new Bitmap(img);
            clone.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            ShowStatus("已截图");
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch (Exception ex)
        {
            ShowStatus("截图失败: " + ex.Message);
        }
    }

    // ---------- 窗口缩放/移动 ----------

    /// <summary>按宽度等比设置窗口大小（跟随画面比例，无黑边）。</summary>
    void SetSizeKeepAspect(int width)
    {
        int w = Math.Clamp(width, MinimumSize.Width, 4000);
        int h = Math.Max(MinimumSize.Height, (int)Math.Round(w / _aspect));
        Size = new Size(w, h);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if (_closing || _fullscreen) return;
        double factor = e.Delta > 0 ? 1.12 : 1 / 1.12;
        Point cursor = PointToScreen(e.Location);
        int newW = Math.Clamp((int)(Width * factor), MinimumSize.Width, 4000);
        double s = (double)newW / Width;
        int newH = Math.Max(MinimumSize.Height, (int)Math.Round(newW / _aspect));
        Location = new Point((int)(cursor.X - (cursor.X - Location.X) * s),
                             (int)(cursor.Y - (cursor.Y - Location.Y) * (double)newH / Height));
        Size = new Size(newW, newH);
    }

    // 等比锁定：拖边缘缩放时强制宽高比
    const int WM_SIZING = 0x216;
    const int WMSZ_LEFT = 1, WMSZ_RIGHT = 2, WMSZ_TOP = 3, WMSZ_TOPLEFT = 4,
              WMSZ_TOPRIGHT = 5, WMSZ_BOTTOM = 6, WMSZ_BOTTOMLEFT = 7, WMSZ_BOTTOMRIGHT = 8;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    struct RECT { public int Left, Top, Right, Bottom; }

    // 老板键：右Ctrl+右Alt+/? 一键隐藏/恢复
    // RegisterHotKey 分不清左右侧修饰键，改用轮询 GetAsyncKeyState（VK_RCONTROL/VK_RMENU 专指右键）
    const int VK_RCONTROL = 0xA3, VK_RMENU = 0xA5, VK_OEM2 = 0xBF; // 0xBF = ?/ 键

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);

    readonly System.Windows.Forms.Timer _bossKeyTimer = new() { Interval = 35 };
    bool _hiddenByBossKey;
    bool _bossChordHeld;

    void ToggleBossKey()
    {
        if (_hiddenByBossKey)
        {
            _hiddenByBossKey = false;
            Show();
            Activate();
        }
        else
        {
            _hiddenByBossKey = true;
            Hide();
        }
    }

    void StartBossKeyWatch()
    {
        _bossKeyTimer.Tick += (_, _) =>
        {
            bool chord = (GetAsyncKeyState(VK_RCONTROL) & 0x8000) != 0
                      && (GetAsyncKeyState(VK_RMENU) & 0x8000) != 0;
            bool slash = (GetAsyncKeyState(VK_OEM2) & 0x8000) != 0;

            if (chord && slash)
            {
                if (!_bossChordHeld)
                {
                    _bossChordHeld = true;
                    ToggleBossKey();
                }
            }
            else if (!slash)
            {
                _bossChordHeld = false; // 松开 /? 键后再按才重新触发
            }
        };
        _bossKeyTimer.Start();
    }

    protected override void WndProc(ref Message m)
    {
        base.WndProc(ref m);

        if (m.Msg == WM_SIZING && !_fullscreen)
        {
            var r = System.Runtime.InteropServices.Marshal.PtrToStructure<RECT>(m.LParam);
            int side = m.WParam.ToInt32();
            int w = r.Right - r.Left;
            int h = r.Bottom - r.Top;
            if (w > 0 && h > 0 && _aspect > 0)
            {
                bool heightDriven = side is WMSZ_TOP or WMSZ_BOTTOM;
                if (heightDriven)
                {
                    w = Math.Max(MinimumSize.Width, (int)Math.Round(h * _aspect));
                    if (side == WMSZ_TOP) r.Left = r.Right - w;
                    else r.Right = r.Left + w;
                }
                else
                {
                    h = Math.Max(MinimumSize.Height, (int)Math.Round(w / _aspect));
                    bool fromTop = side is WMSZ_TOPLEFT or WMSZ_TOPRIGHT or WMSZ_TOP;
                    if (fromTop) r.Top = r.Bottom - h;
                    else r.Bottom = r.Top + h;
                }
                System.Runtime.InteropServices.Marshal.StructureToPtr(r, m.LParam, true);
            }
            m.Result = (IntPtr)1;
            return;
        }

        // 边缘/四角拖拽缩放已按需求移除，缩放只走右键拖动和滚轮
    }

    // ---------- 全屏 ----------

    void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _savedState = WindowState;
            _savedBounds = Bounds;
            FormBorderStyle = FormBorderStyle.None;
            WindowState = FormWindowState.Maximized;
            _fullscreen = true;
            ShowStatus("全屏中 — Esc 或 F11 退出");
        }
        else
        {
            _fullscreen = false;
            WindowState = _savedState;
            Bounds = _savedBounds;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.F11 || (e.KeyCode == Keys.Escape && _fullscreen))
        {
            ToggleFullscreen();
            e.Handled = true;
            return;
        }
        if (e.KeyCode == Keys.S && e.Control) { Snapshot(); e.Handled = true; return; }
        if (e.KeyCode == Keys.T && e.Control)
        {
            _miTop.PerformClick();
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.Up)
        {
            _opacitySlider.Value = Math.Min(_opacitySlider.Maximum, _opacitySlider.Value + 5);
            e.Handled = true;
            return;
        }
        if (e.Control && e.KeyCode == Keys.Down)
        {
            _opacitySlider.Value = Math.Max(_opacitySlider.Minimum, _opacitySlider.Value - 5);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    // ---------- 设置 ----------

    void LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                _settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { _settings = new AppSettings(); }

        var b = new Rectangle(_settings.X, _settings.Y, _settings.Width, _settings.Height);
        if (!Screen.AllScreens.Any(s => s.WorkingArea.IntersectsWith(b)))
        {
            _settings.X = 200;
            _settings.Y = 120;
        }
        Bounds = new Rectangle(_settings.X, _settings.Y,
            Math.Max(_settings.Width, 96), Math.Max(_settings.Height, 54));
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
