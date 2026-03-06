﻿using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

using WinFormsTimer = System.Windows.Forms.Timer;

namespace ClockLangWidget;

internal sealed class OverlayForm : Form
{
    // --- Tunables ---
    private const float UiScaleBase = 0.60f;

    // Lang sync: быстрый первый опрос + один ретрай, если не изменилось
    private const int LangSyncDelayFastMs = 100;
    private const int LangSyncDelayRetryMs = 160;

    // Battery: 5 сегментов = шаг 20%
    private const int BatterySegments = 5;

    // Hover hide (таймер работает ТОЛЬКО когда окно скрыто)
    private const int HoverPollIntervalMs = 200;

    // Worst-case строки для расчёта размеров (чтобы не мерить каждый раз)
    private const string MeasureTimeSample = "88:88";
    private const string MeasureDateSample = "88.88.8888";

    // --- Runtime state ---
    private readonly WinFormsTimer _minuteTimer;
    private readonly WinFormsTimer _langSyncTimer;
    private readonly WinFormsTimer _hoverTimer;

    private IntPtr _taskbarHwnd;
    private bool _exiting;

    // Shell hook (только активация окон)
    private int _shellHookMsg;
    private bool _shellHookRegistered;

    // Low-level keyboard hook (ловим попытку переключения раскладки хоткеями)
    private IntPtr _kbdHook = IntPtr.Zero;
    private LowLevelKeyboardProc? _kbdProc;
    private int _langCheckPending; // 0/1 — чтобы не спамить PostMessage
    private const int WM_APP_CHECK_LANG = 0x8000 + 0x55;

    // HWND кешируем отдельно, чтобы из hook’а не трогать Handle при Dispose
    private IntPtr _hwnd = IntPtr.Zero;

    // Lang sync attempts
    private int _langSyncAttempt;

    // Hover hide state
    private bool _hiddenByHover;
    private int _hoverOutTicks;

    // Какие хоткеи реально отслеживать (можем сузить по настройкам Windows)
    private bool _watchAltShift = true;
    private bool _watchCtrlShift = true;

    // Battery runtime (простая логика)
    private bool _hasBattery;
    private bool _acOnline;   // штекер воткнут => рисуем молнию
    private int _batSteps;    // 0..BatterySegments

    // Layout helpers (computed in EnsureSizeForTextWorstCase)
    private int _leftInsetPx;

    // Scale
    private float _scale = UiScaleBase;

    // Typography
    private Font? _fontTime;
    private Font? _fontLang;
    private Font? _fontDate;

    // Layout metrics (scaled)
    private Padding _pad;
    private int _line1Height;
    private int _line2Height;
    private int _lineGap;
    private int _langShiftLeft;
    private int _shadowMargin;
    private int _minContentWidth;

    // Battery metrics (scaled)
    private int _batIconW;
    private int _batIconH;
    private int _batGap;
    private int _batShiftRight;

    // Text
    private string _timeText = "";
    private string _dateText = "";
    private string _langText = "";

    // Change detection (render key)
    private string _lastRenderKey = "";

    private int _langFixedPx; // фикс ширина под RU/EN/DE

    private readonly WinFormsTimer _fadeTimer = new WinFormsTimer { Interval = 40 }; // 25 FPS
    private const int FadeDurationMs = 250;

    private byte _globalAlpha = 255;

    private int _fadeStartTick;
    private byte _fadeFromAlpha;
    private byte _fadeToAlpha;
    private bool _fading;
    private bool _fadingToHidden;

    private IntPtr _cachedHBitmap = IntPtr.Zero;
    private SIZE _cachedSize;

    public OverlayForm()
    {
        Text = "TrayOverlay";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);

        SystemEvents.DisplaySettingsChanged += OnSystemChanged;
        SystemEvents.UserPreferenceChanged += OnSystemChanged;

        // Callback на питание (plug/unplug и т.п.)
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Таймер на границу минуты
        _minuteTimer = new WinFormsTimer();
        _minuteTimer.Tick += MinuteTimerTick;

        // Lang sync timer (single shot)
        _langSyncTimer = new WinFormsTimer();
        _langSyncTimer.Tick += (_, __) => LangSyncTick();

        // Hover timer — работает только пока окно скрыто
        _hoverTimer = new WinFormsTimer { Interval = HoverPollIntervalMs };
        _hoverTimer.Tick += (_, __) => HoverTick();

        _fadeTimer.Tick += (_, __) => FadeTick();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x00000080;
            const int WS_EX_NOACTIVATE = 0x08000000;
            const int WS_EX_LAYERED = 0x00080000;
            // WS_EX_TRANSPARENT УБРАН: делаем click-through через WM_NCHITTEST/HTTRANSPARENT

            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        _hwnd = Handle;

        UpdateScaleFromDpi();
        RebuildMetricsAndFonts();
        EnsureTaskbarHandle();

        // сузим, какие хоткеи реально включены в системе (опционально, но полезно)
        LoadLayoutSwitchHotkeysFromWindows();

        // Shell hook: ловим смену активного окна
        _shellHookMsg = RegisterWindowMessage("SHELLHOOK");
        _shellHookRegistered = RegisterShellHookWindow(Handle);

        // Low-level keyboard hook
        _kbdProc = KeyboardHookProc;
        _kbdHook = InstallKeyboardHook(_kbdProc);

        // стартовый текст
        var now = DateTime.Now;
        _timeText = now.ToString("HH:mm");
        _dateText = now.ToString("dd.MM.yyyy");
        _langText = GetActiveKeyboardLayoutShort();

        RefreshBattery(); // первичное состояние батареи/AC

        // Пересчёт размеров/позиции — только на старте и при системных изменениях
        RecalculateLayoutAndRender(forceRender: true);

        ScheduleNextMinuteTick();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RefreshAll(force: true);
    }

    protected override void WndProc(ref Message m)
    {
        const int WM_DPICHANGED = 0x02E0;
        const int WM_NCHITTEST = 0x0084;
        const int HTTRANSPARENT = -1;

        if (m.Msg == WM_DPICHANGED)
        {
            base.WndProc(ref m);   // сначала применить suggested rect / внутреннюю логику WinForms

            UpdateScaleFromDpi();
            RebuildMetricsAndFonts();
            RecalculateLayoutAndRender(forceRender: true);
            return;
        }

        // Hover hide + click-through
        if (m.Msg == WM_NCHITTEST)
        {
            // всегда пропускаем мышь “сквозь”
            m.Result = (IntPtr)HTTRANSPARENT;

            // скрываем только если видимы и реально под курсором
            if (!_hiddenByHover && Visible)
            {
                if (Bounds.Contains(Cursor.Position))
                    StartFadeTo(0, hideWhenDone: true);
            }
            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }

        // Shell hook: смена активного окна
        if (_shellHookMsg != 0 && m.Msg == _shellHookMsg)
        {
            int code = m.WParam.ToInt32();
            if (code == HSHELL_WINDOWACTIVATED || code == HSHELL_RUDEAPPACTIVATED)
                RequestLangSyncAfterHotkey();

            base.WndProc(ref m);
            return;
        }

        // Из keyboard hook: “похоже на переключение раскладки”
        if (m.Msg == WM_APP_CHECK_LANG)
        {
            Interlocked.Exchange(ref _langCheckPending, 0);
            RequestLangSyncAfterHotkey();
            base.WndProc(ref m);
            return;
        }

        base.WndProc(ref m);
    }

    private void MinuteTimerTick(object? sender, EventArgs e)
    {
        _minuteTimer.Stop();          // важно: один тик -> один пересчёт
        RefreshMinuteTick();          // твой код обновления текста + RenderOnly
        ScheduleNextMinuteTick();     // снова ровно до следующей минуты
    }

    private void ComputeLangFixedWidth()
    {
        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        float max = 0;
        foreach (var s in new[] { "RU", "EN", "DE" })
            max = Math.Max(max, g.MeasureString(s, _fontLang!).Width);

        _langFixedPx = (int)Math.Ceiling(max);
    }

    // --- Hover tick (показываем, когда курсор ушёл) ---

    private void HoverTick()
    {
        if (!_hiddenByHover)
        {
            _hoverTimer.Stop();
            return;
        }

        // Пока курсор в области окна — остаёмся скрыты
        if (Bounds.Contains(Cursor.Position))
        {
            _hoverOutTicks = 0;
            return;
        }

        // Вышли — можно показывать
        _hoverOutTicks++;
        if (_hoverOutTicks < 1) return;

        _hoverTimer.Stop();
        _hiddenByHover = false;
        _hoverOutTicks = 0;

        // ОБЯЗАТЕЛЬНО: обновить содержимое перед показом, иначе покажешь старый кэш
        var now = DateTime.Now;
        _timeText = now.ToString("HH:mm");
        _dateText = now.ToString("dd.MM.yyyy");
        _langText = GetActiveKeyboardLayoutShort();
        RefreshBattery();          // если батарейка рисуется — тоже актуализируй
        RenderIfNeeded(force: false); // обновит _cachedHBitmap

        StartFadeTo(255, hideWhenDone: false);

        // ВАЖНО: плавно показываем (а не Show+Render с alpha=0)
        StartFadeTo(255, hideWhenDone: false);
    }

    private void StartFadeTo(byte targetAlpha, bool hideWhenDone)
    {
        if (IsDisposed || Disposing) return;

        // если уже в нужном состоянии — ничего
        if (!_fading && _globalAlpha == targetAlpha) return;

        if (_fading && _fadeToAlpha == targetAlpha && _fadingToHidden == hideWhenDone) return;

        // если окно скрыто, а надо показывать — покажем сразу (но с alpha=0)
        if (!Visible && targetAlpha > 0)
        {
            _globalAlpha = 0;
            Show();
            UpdateLayered(); // отрисовать с alpha=0
            EnsureBehindTaskbar();
        }

        _fading = true;
        _fadingToHidden = hideWhenDone;

        _fadeFromAlpha = _globalAlpha;
        _fadeToAlpha = targetAlpha;
        _fadeStartTick = Environment.TickCount;

        _fadeTimer.Start();
    }

    private void FadeTick()
    {
        if (!_fading)
        {
            _fadeTimer.Stop();
            return;
        }

        // маленький UX-бонус: если начали fade-out, но курсор уже ушёл — разворачиваем на fade-in
        bool hovered = Bounds.Contains(Cursor.Position);
        if (_fadingToHidden && !hovered)
        {
            StartFadeTo(255, hideWhenDone: false);
            return;
        }
        if (!_fadingToHidden && hovered)
        {
            StartFadeTo(0, hideWhenDone: true);
            return;
        }

        int elapsed = unchecked(Environment.TickCount - _fadeStartTick);
        float t = Math.Clamp(elapsed / (float)FadeDurationMs, 0f, 1f);

        int a = (int)Math.Round(_fadeFromAlpha + (_fadeToAlpha - _fadeFromAlpha) * t);
        _globalAlpha = (byte)Math.Clamp(a, 0, 255);

        if (_cachedHBitmap != IntPtr.Zero)
            SetLayeredHBitmap(_cachedHBitmap, _cachedSize, Location, _globalAlpha);
        else
            UpdateLayered(); // на всякий случай, если ещё ни разу не рендерили

        if (t >= 1f)
        {
            _fadeTimer.Stop();
            _fading = false;
            _globalAlpha = _fadeToAlpha;

            if (_fadingToHidden && _globalAlpha == 0)
            {
                Hide(); // реально спрятали
                // дальше можно оставить твой hoverTimer-поллинг, чтобы показать когда мышь уйдёт
                _hiddenByHover = true;
                _hoverTimer.Start();
            }
        }
    }

    // --- Refresh routines ---

    private void RefreshMinuteTick()
    {
        if (IsDisposed || Disposing) return;

        var now = DateTime.Now;
        _timeText = now.ToString("HH:mm");
        _dateText = now.ToString("dd.MM.yyyy");

        // Батарейку в минутном тике НЕ опрашиваем (только по событию)
        RenderOnly(force: false);
    }

    private void RefreshLangOnly()
    {
        if (IsDisposed || Disposing) return;

        var newLang = GetActiveKeyboardLayoutShort();
        if (newLang == _langText)
        {
            EnsureBehindTaskbar();
            return;
        }

        _langText = newLang;
        RenderOnly(force: false);
    }

    private void RefreshAll(bool force)
    {
        if (IsDisposed || Disposing) return;

        var now = DateTime.Now;
        _timeText = now.ToString("HH:mm");
        _dateText = now.ToString("dd.MM.yyyy");
        _langText = GetActiveKeyboardLayoutShort();

        bool oldHas = _hasBattery;
        RefreshBattery();

        if (oldHas != _hasBattery)
        {
            RecalculateLayoutAndRender(forceRender: force);
        }
        else
        {
            RenderOnly(force: force);
        }
    }

    private void RenderOnly(bool force)
    {
        if (IsDisposed || Disposing) return;
        if (_hiddenByHover) return; // пока скрыты — не тратим CPU на UpdateLayered

        RenderIfNeeded(force);
        EnsureBehindTaskbar();
    }

    // --- Power callback (plug/unplug etc.) ---

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (IsDisposed || Disposing) return;

        // фильтруем, чтобы лишний раз не дёргать UI
        if (e.Mode != PowerModes.StatusChange)
            return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(OnPowerModeChangedUi)); } catch { }
            return;
        }

        OnPowerModeChangedUi();
    }

    private void OnPowerModeChangedUi()
    {
        if (IsDisposed || Disposing) return;

        bool oldHas = _hasBattery;
        bool oldAc = _acOnline;
        int oldSteps = _batSteps;

        RefreshBattery();

        if (oldHas != _hasBattery)
        {
            RecalculateLayoutAndRender(forceRender: false);
            return;
        }

        if (oldAc != _acOnline || oldSteps != _batSteps)
            RenderOnly(force: false);
    }

    // --- “Sync lang after hotkey” ---

    private void RequestLangSyncAfterHotkey()
    {
        if (IsDisposed || Disposing) return;

        _langSyncAttempt = 0;
        _langSyncTimer.Stop();
        _langSyncTimer.Interval = LangSyncDelayFastMs;
        _langSyncTimer.Start();
    }

    private void LangSyncTick()
    {
        _langSyncTimer.Stop();
        if (IsDisposed || Disposing) return;

        var cur = GetActiveKeyboardLayoutShort();
        if (cur != _langText)
        {
            _langText = cur;
            RenderOnly(force: false);
            return;
        }

        // Не изменилось — один ретрай
        if (_langSyncAttempt++ == 0)
        {
            _langSyncTimer.Interval = LangSyncDelayRetryMs;
            _langSyncTimer.Start();
            return;
        }

        EnsureBehindTaskbar();
    }

    // --- Battery state (AC online => молния) ---

    private void RefreshBattery()
    {
        if (!GetSystemPowerStatus(out var sps))
        {
            _hasBattery = false;
            _acOnline = false;
            _batSteps = 0;
            return;
        }

        // 0x80 = NoSystemBattery, 0xFF = Unknown
        if (sps.BatteryFlag == 0x80)
        {
            _hasBattery = false;
            _acOnline = (sps.ACLineStatus == 1);
            _batSteps = 0;
            return;
        }

        if (sps.BatteryFlag == 0xFF)
        {
            // статус батареи неизвестен — не дёргаем _hasBattery/_batSteps, чтобы не мигало
            _acOnline = (sps.ACLineStatus == 1);
            return;
        }

        _hasBattery = true;
        _acOnline = (sps.ACLineStatus == 1);

        // проценты могут быть "неизвестно" (255) — тогда оставим прошлое значение
        if (sps.BatteryLifePercent != 255)
        {
            float p = sps.BatteryLifePercent / 100f;
            if (p < 0) p = 0;
            if (p > 1) p = 1;

            int steps = (int)Math.Round(p * BatterySegments, MidpointRounding.AwayFromZero);
            if (p > 0 && steps == 0) steps = 1;
            _batSteps = Math.Clamp(steps, 0, BatterySegments);
        }
        else
        {
            if (_batSteps < 0 || _batSteps > BatterySegments)
                _batSteps = BatterySegments;
        }
    }



    // --- Layout + render (редко) ---

    private void RecalculateLayoutAndRender(bool forceRender)
    {
        if (IsDisposed || Disposing) return;

        EnsureTaskbarHandle();
        EnsureSizeForTextWorstCase();
        RepositionNearBottomRightIfNeeded();
        RenderIfNeeded(forceRender);
        EnsureBehindTaskbar();
    }

    // --- Render pipeline ---

    private void RenderIfNeeded(bool force)
    {
        string key =
            $"{_scale:F4}|{Width}x{Height}|{Location.X},{Location.Y}|{_langText}|{_timeText}|{_dateText}|BAT:{(_hasBattery ? _batSteps : -1)}|AC:{(_acOnline ? 1 : 0)}|LI:{_leftInsetPx}";
        if (!force && key == _lastRenderKey)
            return;

        _lastRenderKey = key;
        UpdateLayered();
    }

    private void UpdateLayered()
    {
        if (!IsHandleCreated) return;
        if (_hwnd == IntPtr.Zero) return;

        int w = Width;
        int h = Height;

        using var bmp = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);

            g.SmoothingMode = SmoothingMode.HighQuality;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            var panel = new Rectangle(_shadowMargin, _shadowMargin, w - _shadowMargin * 2, h - _shadowMargin * 2);
            DrawPanelWin11Like(g, panel);

            var inner = panel;
            int textW = Math.Max(1, inner.Width - _pad.Horizontal);

            int leftInset = Math.Clamp(_leftInsetPx, 0, Math.Max(0, textW - 1));

            var rcTime = new Rectangle(
                inner.X + _pad.Left + leftInset,
                inner.Y + _pad.Top,
                Math.Max(1, textW - leftInset),
                _line1Height);

            var rcDate = new Rectangle(
                inner.X + _pad.Left + leftInset,
                inner.Y + _pad.Top + _line1Height + _lineGap,
                Math.Max(1, textW - leftInset),
                _line2Height);

            int langY = inner.Y + (inner.Height - _line2Height) / 2;

            // Язык рисуем в “резервированном” прямоугольнике (worst-case)
            var rcLang = new Rectangle(
                inner.X + _pad.Left - _langShiftLeft,
                langY,
                Math.Max(1, _langFixedPx + _langShiftLeft + 4),
                _line2Height);

            // батарейка на уровне языка: ставим после резервированного места под язык
            Rectangle? rcBat = null;
            if (_hasBattery)
            {
                int afterLang = Math.Max(0, (-_langShiftLeft + _langFixedPx));
                int batX = inner.X + _pad.Left + afterLang + _batGap + _batShiftRight;
                int batY = langY + (_line2Height - _batIconH) / 2;
                rcBat = new Rectangle(batX, batY, _batIconW, _batIconH);
            }

            using var sfRight = new StringFormat { Alignment = StringAlignment.Far, LineAlignment = StringAlignment.Center };
            using var sfLeft = new StringFormat { Alignment = StringAlignment.Near, LineAlignment = StringAlignment.Center };

            DrawTextModern(g, _langText, _fontLang!, rcLang, sfLeft,
                shadowAlpha: 115, outlineAlpha: 55, outlineWidth: 1.05f * _scale, shadowOffset: 1.1f * _scale);

            if (rcBat.HasValue)
                DrawBatteryModern(g, rcBat.Value, _batSteps, BatterySegments, _acOnline);

            DrawTextModern(g, _timeText, _fontTime!, rcTime, sfRight,
                shadowAlpha: 125, outlineAlpha: 65, outlineWidth: 1.05f * _scale, shadowOffset: 1.15f * _scale);

            DrawTextModern(g, _dateText, _fontDate!, rcDate, sfRight,
                shadowAlpha: 105, outlineAlpha: 45, outlineWidth: 1.05f * _scale, shadowOffset: 1.0f * _scale);
        }

        // обновляем кэш HBITMAP (и чистим старый)
        IntPtr newHb = bmp.GetHbitmap(Color.FromArgb(0));
        if (_cachedHBitmap != IntPtr.Zero) DeleteObject(_cachedHBitmap);
        _cachedHBitmap = newHb;
        _cachedSize = new SIZE(bmp.Width, bmp.Height);

        // показать из кэша
        SetLayeredHBitmap(_cachedHBitmap, _cachedSize, Location, _globalAlpha);
    }

    private void DrawPanelWin11Like(Graphics g, Rectangle panel)
    {
        using (var bg = new SolidBrush(Color.FromArgb(205, 28, 28, 28)))
            g.FillRectangle(bg, panel);

        var borderRect = new Rectangle(panel.X, panel.Y, panel.Width - 1, panel.Height - 1);
        using (var pen = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
            g.DrawRectangle(pen, borderRect);
    }

    // Размер считается по “worst-case” строкам и пересчитывается редко
    private void EnsureSizeForTextWorstCase()
    {
        int contentHeight = _pad.Vertical + _line1Height + _lineGap + _line2Height;

        using var tmp = new Bitmap(1, 1);
        using var g = Graphics.FromImage(tmp);
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        // небольшой запас, чтобы правый блок не "дышал" из-за рендеринга
        float safety = 16f * _scale;

        // worst-case для правого блока
        float wTime = g.MeasureString(MeasureTimeSample, _fontTime!).Width; // "88:88"
        float wDate = g.MeasureString(MeasureDateSample, _fontDate!).Width; // "88.88.8888"

        // На всякий случай: если _langFixedPx не рассчитан (должен быть рассчитан в RebuildMetricsAndFonts)
        if (_langFixedPx <= 0)
            _langFixedPx = (int)Math.Ceiling(g.MeasureString("RU", _fontLang!).Width);

        // left inset: конец языка (с учётом сдвига) + зазор + (батарея + зазор)
        float langEndFromPad = -_langShiftLeft + _langFixedPx; // конец языка относительно inner.X + pad.Left
        if (langEndFromPad < 0) langEndFromPad = 0;

        float left = langEndFromPad + _batGap;
        if (_hasBattery)
            left += _batIconW + _batGap;

        _leftInsetPx = (int)Math.Ceiling(left);

        // Итоговая требуемая ширина контента: левый inset + правый текст + запас + паддинги
        float needTime = _leftInsetPx + wTime + safety + _pad.Horizontal;
        float needDate = _leftInsetPx + wDate + safety + _pad.Horizontal;

        int contentWidth = (int)Math.Ceiling(Math.Max(_minContentWidth, Math.Max(needTime, needDate)));

        int width = contentWidth + _shadowMargin * 2;
        int height = contentHeight + _shadowMargin * 2;

        if (ClientSize.Width != width || ClientSize.Height != height)
            ClientSize = new Size(width, height);
    }

    private void RepositionNearBottomRightIfNeeded()
    {
        var screen = Screen.PrimaryScreen!;
        if (_taskbarHwnd != IntPtr.Zero)
        {
            var r = GetWindowRectSafe(_taskbarHwnd);
            if (r.HasValue)
            {
                var tbCenter = new Point((r.Value.Left + r.Value.Right) / 2, (r.Value.Top + r.Value.Bottom) / 2);
                screen = Screen.FromPoint(tbCenter);
            }
        }

        Rectangle bounds = screen.Bounds;

        int bx = _shadowMargin;
        int by = _shadowMargin;
        int panelW = Width - bx * 2;
        int panelH = Height - by * 2;

        var newLoc = new Point(bounds.Right - panelW - bx, bounds.Bottom - panelH - by);
        if (Location != newLoc)
            Location = newLoc;
    }

    private void EnsureBehindTaskbar()
    {
        if (!IsHandleCreated) return;

        if (_taskbarHwnd == IntPtr.Zero || !IsWindow(_taskbarHwnd))
            _taskbarHwnd = FindWindow("Shell_TrayWnd", null);

        if (_taskbarHwnd == IntPtr.Zero) return;

        SetWindowPos(Handle, _taskbarHwnd, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private void EnsureTaskbarHandle()
    {
        if (_taskbarHwnd == IntPtr.Zero || !IsWindow(_taskbarHwnd))
            _taskbarHwnd = FindWindow("Shell_TrayWnd", null);
    }

    private void ScheduleNextMinuteTick()
    {
        var now = DateTime.Now;
        var next = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0).AddMinutes(1);
        int ms = (int)Math.Clamp((next - now).TotalMilliseconds + 25, 250, 60_000);

        _minuteTimer.Interval = ms;
        _minuteTimer.Start();
    }

    private void UpdateScaleFromDpi()
    {
        float dpiScale = 1f;

        if (IsHandleCreated)
        {
            try
            {
                uint dpi = GetDpiForWindow(Handle);
                if (dpi >= 96) dpiScale = dpi / 96f;
            }
            catch { }
        }

        if (dpiScale <= 0.1f) dpiScale = 1f;
        _scale = UiScaleBase * dpiScale;
    }

    private void RebuildMetricsAndFonts()
    {
        _pad = new Padding(
            (int)Math.Round(10 * _scale),
            (int)Math.Round(7 * _scale),
            (int)Math.Round(12 * _scale),
            (int)Math.Round(9 * _scale));

        _line1Height = (int)Math.Ceiling(28 * _scale);
        _line2Height = (int)Math.Ceiling(18 * _scale);
        _lineGap = (int)Math.Ceiling(6 * _scale);
        _langShiftLeft = (int)Math.Ceiling(10 * _scale);

        _shadowMargin = (int)Math.Ceiling(10 * _scale);
        _minContentWidth = (int)Math.Ceiling(170 * _scale);

        // battery icon metrics (aligned to lang row)
        _batIconW = (int)Math.Ceiling(31.2f * _scale); // +20% horizontal size
        _batIconH = (int)Math.Ceiling(14 * _scale);
        _batGap = (int)Math.Ceiling(_scale);
        _batShiftRight = Math.Max(1, (int)Math.Round(15 * _scale));

        _fontTime?.Dispose();
        _fontLang?.Dispose();
        _fontDate?.Dispose();

        _fontTime = CreateFont(
            new[] { "Segoe UI Variable Display Semibold", "Segoe UI Semibold", "Segoe UI" },
            12.0f * _scale, FontStyle.Regular);

        _fontLang = CreateFont(
            new[] { "Segoe UI Variable Display", "Segoe UI Semibold", "Segoe UI" },
            10.0f * _scale, FontStyle.Regular);
        ComputeLangFixedWidth();

        _fontDate = CreateFont(
            new[] { "Segoe UI Variable Text", "Segoe UI", "Segoe UI Semilight" },
            8.25f * _scale, FontStyle.Regular);
    }

    // --- SystemEvents: обязательно в UI-поток ---

    private void OnSystemChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(new Action(OnSystemChangedUi)); }
            catch { }
            return;
        }

        OnSystemChangedUi();
    }

    private void OnSystemChangedUi()
    {
        if (IsDisposed || Disposing) return;

        EnsureTaskbarHandle();
        LoadLayoutSwitchHotkeysFromWindows();

        UpdateScaleFromDpi();
        RebuildMetricsAndFonts();

        RecalculateLayoutAndRender(forceRender: true);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnSystemChanged;
            SystemEvents.UserPreferenceChanged -= OnSystemChanged;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;

            try { _minuteTimer.Stop(); _minuteTimer.Dispose(); } catch { }
            try { _langSyncTimer.Stop(); _langSyncTimer.Dispose(); } catch { }
            try { _hoverTimer.Stop(); _hoverTimer.Dispose(); } catch { }
            try { _fadeTimer.Stop(); _fadeTimer.Dispose(); } catch { }

            // Сначала делаем hwnd недоступным для hook’а
            _hwnd = IntPtr.Zero;

            if (_kbdHook != IntPtr.Zero)
            {
                try { UnhookWindowsHookEx(_kbdHook); } catch { }
                _kbdHook = IntPtr.Zero;
                _kbdProc = null;
            }

            if (_shellHookRegistered)
            {
                try { DeregisterShellHookWindow(Handle); } catch { }
                _shellHookRegistered = false;
            }

            _fontTime?.Dispose();
            _fontLang?.Dispose();
            _fontDate?.Dispose();
        }

        if (_cachedHBitmap != IntPtr.Zero)
        {
            try { DeleteObject(_cachedHBitmap); } catch { }
            _cachedHBitmap = IntPtr.Zero;
        }
        base.Dispose(disposing);

        if (_exiting)
            Application.ExitThread();
    }

    // ---- Rendering helpers ----

    private static void DrawTextModern(Graphics g, string text, Font font, Rectangle rect, StringFormat sf,
        int shadowAlpha, int outlineAlpha, float outlineWidth, float shadowOffset)
    {
        if (string.IsNullOrEmpty(text)) return;

        using var path = new GraphicsPath();
        float emSize = font.SizeInPoints * g.DpiY / 72f;
        path.AddString(text, font.FontFamily, (int)font.Style, emSize, rect, sf);

        using (var sp = (GraphicsPath)path.Clone())
        using (var m = new Matrix())
        using (var sb = new SolidBrush(Color.FromArgb(shadowAlpha, 0, 0, 0)))
        {
            m.Translate(shadowOffset, shadowOffset);
            sp.Transform(m);
            g.FillPath(sb, sp);
        }

        using (var pen = new Pen(Color.FromArgb(outlineAlpha, 0, 0, 0), outlineWidth)
        {
            LineJoin = LineJoin.Round,
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        })
        {
            g.DrawPath(pen, path);
        }

        using var tb = new SolidBrush(Color.FromArgb(242, 255, 255, 255));
        g.FillPath(tb, path);
    }

    private void DrawBatteryModern(Graphics g, Rectangle r, int steps, int maxSteps, bool showBolt)
    {
        float stroke = Math.Max(1f, 1.15f * _scale);
        int capW = Math.Max(2, (int)Math.Ceiling(3 * _scale));
        int rad = Math.Max(1, (int)Math.Ceiling(2 * _scale));

        var body = new Rectangle(r.X, r.Y, Math.Max(1, r.Width - capW - 1), r.Height);
        var cap = new Rectangle(
            body.Right + 1,
            r.Y + (int)Math.Round(r.Height * 0.28f),
            capW,
            Math.Max(1, (int)Math.Round(r.Height * 0.44f)));

        using var borderPen = new Pen(Color.FromArgb(160, 255, 255, 255), stroke);

        using (var path = RoundedRect(body, rad))
            g.DrawPath(borderPen, path);

        using (var capBrush = new SolidBrush(Color.FromArgb(160, 255, 255, 255)))
            g.FillRectangle(capBrush, cap);

        int inset = Math.Max(1, (int)Math.Ceiling(2 * _scale));
        var inner = Rectangle.Inflate(body, -inset, -inset);
        if (inner.Width <= 0 || inner.Height <= 0) return;

        steps = Math.Clamp(steps, 0, maxSteps);

        int fillW = (int)Math.Round(inner.Width * (steps / (float)maxSteps));
        if (fillW > 0)
        {
            var fill = new Rectangle(inner.X, inner.Y, fillW, inner.Height);
            using var fillBrush = new SolidBrush(Color.FromArgb(220, 255, 255, 255));
            g.FillRectangle(fillBrush, fill);
        }

        if (maxSteps > 1)
        {
            using var sepPen = new Pen(Color.FromArgb(40, 0, 0, 0), Math.Max(1f, 1f * _scale));
            for (int i = 1; i < maxSteps; i++)
            {
                int x = inner.X + (int)Math.Round(inner.Width * (i / (float)maxSteps));
                g.DrawLine(sepPen, x, inner.Y, x, inner.Bottom);
            }
        }

        // Молния рисуется последней, чтобы быть поверх батарейки.
        if (showBolt)
        {
            using var bolt = MakeBoltPath(body);

            using var boltBrush = new SolidBrush(Color.FromArgb(245, 0, 0, 0));
            g.FillPath(boltBrush, bolt);

            using var boltPen = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1f, 1.2f * _scale))
            {
                LineJoin = LineJoin.Round,
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawPath(boltPen, bolt);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var path = new GraphicsPath();
        if (radius <= 0)
        {
            path.AddRectangle(r);
            path.CloseFigure();
            return path;
        }

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private GraphicsPath MakeBoltPath(Rectangle body)
    {
        // Windows-like charging bolt: intentionally protrudes above battery body.
        float cx = body.X + body.Width * 0.50f;
        float top = body.Y - body.Height * 0.34f;
        float midTop = body.Y + body.Height * 0.06f;
        float midBottom = body.Y + body.Height * 0.46f;
        float bottom = body.Y + body.Height * 0.90f;
        float w = body.Width * 0.56f;

        var p = new GraphicsPath();
        p.AddPolygon(new[]
        {
            new PointF(cx - w * 0.34f, top),
            new PointF(cx + w * 0.04f, top),
            new PointF(cx - w * 0.08f, midTop),
            new PointF(cx + w * 0.36f, midTop),
            new PointF(cx - w * 0.08f, bottom),
            new PointF(cx + w * 0.02f, midBottom),
            new PointF(cx - w * 0.38f, midBottom),
        });
        return p;
    }

    private static Font CreateFont(string[] preferredNames, float size, FontStyle style)
    {
        foreach (var name in preferredNames)
        {
            try { return new Font(name, size, style, GraphicsUnit.Point); }
            catch { }
        }
        return new Font("Segoe UI", size, style, GraphicsUnit.Point);
    }

    private static RECT? GetWindowRectSafe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        return GetWindowRect(hwnd, out RECT r) ? r : null;
    }

    private static string GetActiveKeyboardLayoutShort()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return "";

        uint tid = GetWindowThreadProcessId(fg, out _);
        IntPtr hkl = GetKeyboardLayout(tid);

        int langId = (int)((ulong)hkl & 0xFFFF);
        try
        {
            var ci = CultureInfo.GetCultureInfo(langId);
            return ci.TwoLetterISOLanguageName.ToUpperInvariant();
        }
        catch
        {
            return $"0x{langId:X4}";
        }
    }

    private void SetLayeredBitmap(Bitmap bmp, Point screenPos, byte alpha)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);

        IntPtr hBitmap = IntPtr.Zero;
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            hBitmap = bmp.GetHbitmap(Color.FromArgb(0));
            oldBitmap = SelectObject(memDc, hBitmap);

            var size = new SIZE(bmp.Width, bmp.Height);
            var src = new POINT(0, 0);
            var dst = new POINT(screenPos.X, screenPos.Y);

            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,   // <-- ВАЖНО
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            if (hBitmap != IntPtr.Zero) DeleteObject(hBitmap);

            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private void SetLayeredHBitmap(IntPtr hBitmap, SIZE size, Point screenPos, byte alpha)
    {
        if (hBitmap == IntPtr.Zero) return;

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr oldBitmap = IntPtr.Zero;

        try
        {
            oldBitmap = SelectObject(memDc, hBitmap);

            var src = new POINT(0, 0);
            var dst = new POINT(screenPos.X, screenPos.Y);

            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = alpha,
                AlphaFormat = AC_SRC_ALPHA
            };

            UpdateLayeredWindow(Handle, screenDc, ref dst, ref size, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (oldBitmap != IntPtr.Zero) SelectObject(memDc, oldBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ---- Keyboard hook (filtered) ----

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (_exiting || _hwnd == IntPtr.Zero)
            return CallNextHookEx(_kbdHook, nCode, wParam, lParam);

        if (nCode >= 0)
        {
            int msg = wParam.ToInt32();

            // Реакция на KEYUP: переключение чаще всего происходит на отпускании
            if (msg == WM_KEYUP || msg == WM_SYSKEYUP)
            {
                int vk = Marshal.ReadInt32(lParam); // vkCode из KBDLLHOOKSTRUCT (первое поле)

                if (IsLikelyLayoutSwitchKeyOnKeyUp(vk))
                {
                    if (Interlocked.Exchange(ref _langCheckPending, 1) == 0)
                        PostMessage(_hwnd, WM_APP_CHECK_LANG, IntPtr.Zero, IntPtr.Zero);
                }
            }
        }

        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private bool IsLikelyLayoutSwitchKeyOnKeyUp(int vk)
    {
        // Win+Space — не отслеживаем

        if (_watchAltShift)
        {
            if ((vk == VK_LSHIFT || vk == VK_RSHIFT) && (IsDown(VK_LMENU) || IsDown(VK_RMENU)))
                return true;

            if ((vk == VK_LMENU || vk == VK_RMENU) && (IsDown(VK_LSHIFT) || IsDown(VK_RSHIFT)))
                return true;
        }

        if (_watchCtrlShift)
        {
            if ((vk == VK_LSHIFT || vk == VK_RSHIFT) && (IsDown(VK_LCONTROL) || IsDown(VK_RCONTROL)))
                return true;

            if ((vk == VK_LCONTROL || vk == VK_RCONTROL) && (IsDown(VK_LSHIFT) || IsDown(VK_RSHIFT)))
                return true;
        }

        return false;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static IntPtr InstallKeyboardHook(LowLevelKeyboardProc proc)
    {
        IntPtr hMod = GetModuleHandle(null);
        return SetWindowsHookEx(WH_KEYBOARD_LL, proc, hMod, 0);
    }

    // --- Read configured hotkeys from Windows (optional narrowing) ---

    private void LoadLayoutSwitchHotkeysFromWindows()
    {
        bool alt = false;
        bool ctrl = false;
        bool haveAny = false;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Keyboard Layout\Toggle");
            if (key != null)
            {
                // 1=Alt+Shift, 2=Ctrl+Shift, 3=Disabled, 4=Grave accent (не поддерживаем)
                if (TryReadToggleHotkey(key, "Language Hotkey", out int codeLang) ||
                    TryReadToggleHotkey(key, "Hotkey", out codeLang))
                {
                    haveAny = true;
                    ApplyToggleCode(codeLang, ref alt, ref ctrl);
                }

                if (TryReadToggleHotkey(key, "Layout Hotkey", out int codeLayout))
                {
                    haveAny = true;
                    ApplyToggleCode(codeLayout, ref alt, ref ctrl);
                }
            }
        }
        catch
        {
            // ignore; fall back to defaults
        }

        if (!haveAny)
        {
            _watchAltShift = true;
            _watchCtrlShift = true;
            return;
        }

        _watchAltShift = alt;
        _watchCtrlShift = ctrl;
    }

    private static void ApplyToggleCode(int code, ref bool alt, ref bool ctrl)
    {
        switch (code)
        {
            case 1: alt = true; break;
            case 2: ctrl = true; break;
        }
    }

    private static bool TryReadToggleHotkey(RegistryKey key, string valueName, out int code)
    {
        code = 0;

        object? v = key.GetValue(valueName);
        if (v == null) return false;

        if (v is int i)
        {
            code = i;
            return true;
        }

        string? s = v.ToString();
        if (string.IsNullOrWhiteSpace(s)) return false;

        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int dec))
        {
            code = dec;
            return true;
        }

        if (int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex))
        {
            code = hex;
            return true;
        }

        return false;
    }

    #region Win32

    // Shell hook codes
    private const int HSHELL_WINDOWACTIVATED = 4;
    private const int HSHELL_RUDEAPPACTIVATED = 0x8004;

    // Low-level keyboard
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYUP = 0x0105;

    // VK codes
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4; // Alt
    private const int VK_RMENU = 0xA5; // Alt

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterShellHookWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DeregisterShellHookWindow(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string lpString);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    // Layered window
    private const int ULW_ALPHA = 0x00000002;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
        int crKey, ref BLENDFUNCTION pblend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = false)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS sps);

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;        // 0=Offline, 1=Online, 255=Unknown
        public byte BatteryFlag;         // 128=No battery
        public byte BatteryLifePercent;  // 0-100, 255=Unknown
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
        public SIZE(int x, int y) { cx = x; cy = y; }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    #endregion
}
