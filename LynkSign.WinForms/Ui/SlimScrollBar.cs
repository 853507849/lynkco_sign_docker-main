using System.Runtime.InteropServices;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 挂在 <see cref="RichTextBox"/> 右边的一根自绘细滚动条。
///
/// <para>
/// 为什么要自己画：<c>RichTextBox</c> 的滚动条是 RichEdit 内部画的，WinForms 层
/// 既不能换色也不能改宽度。一根 17px 宽的白色轨道压在深色日志卡上，等于在
/// 一块深色面板上贴了条白胶布 —— 界面显脏，有相当一部分就来自这里。
/// </para>
///
/// <para>
/// 做法：<b>不</b>关掉 RichTextBox 的滚动能力（关掉之后滚轮、光标跟随都会失效），
/// 而是保留 <c>ScrollBars = ForcedVertical</c>，把原生滚动条 <c>ShowScrollBar(false)</c>
/// 隐藏掉，旁边另起一根 10px 宽的自绘条。滚动位置靠 <c>GetScrollInfo</c> 读，
/// 拖拽与滚轮靠 <c>WM_VSCROLL</c> / <c>EM_LINESCROLL</c> 写回 —— 走的都是 RichEdit
/// 自己的滚动逻辑，所以「追加日志后滚到底部」这类行为与原生完全一致。
/// </para>
///
/// <para>
/// ⚠️ 隐藏必须<b>持续压制</b>（<see cref="NativeScroll.Suppress"/>），不能只调用一次：
/// 控件在滚轮、按键、重排时会把 WS_VSCROLL 自己装回去，只藏一次的后果是
/// 用户滚一下滚轮就同时看到自绘条和原生白条两根。
/// </para>
///
/// <para>
/// 宿主三步：<c>new SlimScrollBar(...)</c> → 摆位 → <c>Attach(richTextBox)</c>。
/// 之后控件自己订阅 VScroll / Resize / TextChanged 保持同步。
/// </para>
/// </summary>
internal sealed class SlimScrollBar : Control
{
    /// <summary>建议占用的宽度（含两侧留白）。宿主按这个值在右侧留位置。</summary>
    public const int BarWidth = 10;

    /// <summary>滑块离控件左右边缘的留白，剩下的才是滑块本体（10 - 2*2 = 6px）。</summary>
    private const int Inset = 2;

    /// <summary>滑块最短高度：内容极长时也要留出能抓住的一段。</summary>
    private const int MinThumb = 30;

    private const int WM_VSCROLL = 0x0115;
    private const int SB_THUMBTRACK = 5;
    private const int EM_LINESCROLL = 0x00B6;
    private const int SIF_ALL = 0x17;

    private readonly Color _thumb;
    private readonly Color _thumbHover;

    private RichTextBox? _target;
    private IDisposable? _suppress;
    private bool _hover;
    private bool _dragging;
    private int _dragOffset;
    private int _thumbY;
    private int _thumbH;
    private bool _overflow;

    public SlimScrollBar(Color thumb, Color thumbHover)
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        // 不参与焦点循环：Tab 应该落在输入框上，而不是这根装饰条上
        SetStyle(ControlStyles.Selectable, false);

        _thumb = thumb;
        _thumbHover = thumbHover;

        Width = BarWidth;
    }

    /// <summary>接上要控制的那个文本框。重复调用会先断开旧的那个。</summary>
    public void Attach(RichTextBox box)
    {
        Detach();

        _target = box;
        box.VScroll += OnTargetChanged;
        box.TextChanged += OnTargetChanged;
        box.Resize += OnTargetChanged;
        box.HandleCreated += OnTargetChanged;

        // 只在外层"按一下"是压不住的：RichEdit 每次滚轮、按键、重排都会把 WS_VSCROLL
        // 重新装回窗口样式上（自己调 ShowScrollBar(TRUE)），于是滚一下就多出第二根白条。
        // 这里挂窗口子类化，谁把它装回来就在同一个消息里再藏掉。
        _suppress = NativeScroll.Suppress(box, Sync);

        if (box.IsHandleCreated) Sync();
    }

    public void Detach()
    {
        if (_target is not { } box) return;

        box.VScroll -= OnTargetChanged;
        box.TextChanged -= OnTargetChanged;
        box.Resize -= OnTargetChanged;
        box.HandleCreated -= OnTargetChanged;

        _suppress?.Dispose();
        _suppress = null;

        _target = null;
    }

    /// <summary>重新读一次滚动状态并重绘。宿主批量改完文本后可以主动调一次。</summary>
    public void Sync()
    {
        if (_target is not { IsHandleCreated: true } box) return;

        // RichEdit 在每次重排（改宽度、换字体、内容暴涨）时都可能把滚动条自己重新显示出来，
        // 所以每次同步都顺手再藏一遍 —— 这个调用很便宜，比盯着 Resize 猜时机可靠。
        NativeScroll.HideVertical(box.Handle);

        if (!TryRead(out var info))
        {
            SetOverflow(false);
            return;
        }

        var range = Math.Max(1, info.nMax - info.nMin + 1);
        var page = Math.Max(1, info.nPage);

        if (range <= page)
        {
            SetOverflow(false);
            return;
        }

        SetOverflow(true);

        var track = Math.Max(0, Height - Inset * 2);
        _thumbH = Math.Clamp((int)Math.Round(track * (double)page / range), MinThumb, Math.Max(MinThumb, track));

        var maxScroll = Math.Max(1, range - page);
        var ratio = Math.Clamp((info.nPos - info.nMin) / (double)maxScroll, 0D, 1D);
        _thumbY = Inset + (int)Math.Round((track - _thumbH) * ratio);

        Invalidate();
    }

    private void SetOverflow(bool overflow)
    {
        if (_overflow == overflow) return;

        _overflow = overflow;
        _thumbH = 0;
        Invalidate();
    }

    private bool TryRead(out ScrollInfo info)
    {
        info = new ScrollInfo
        {
            cbSize = Marshal.SizeOf<ScrollInfo>(),
            fMask = SIF_ALL,
        };

        return _target is { IsHandleCreated: true } box && NativeScroll.ReadVertical(box.Handle, ref info);
    }

    // ---------- 目标控件的事件 ----------

    private void OnTargetChanged(object? sender, EventArgs e) => Sync();

    // ---------- 自绘 ----------

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        // 自绘控件第一句永远是刷底：漏了就会把双缓冲缓冲区里的残留像素 blit 到屏幕上
        Theme.ClearBackdrop(g, this);

        if (!_overflow || _thumbH <= 0) return;

        var fill = _dragging || _hover ? _thumbHover : _thumb;
        var w = Math.Max(1, Width - Inset * 2);

        Theme.FillRound(g, new RectangleF(Inset, _thumbY, w, _thumbH), w / 2F, fill);
    }

    // ---------- 拖拽与滚轮 ----------

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        if (!_dragging) Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !_overflow) return;

        if (e.Y >= _thumbY && e.Y <= _thumbY + _thumbH)
        {
            // 抓在滑块上：记住抓点相对滑块顶部的距离，拖动时滑块才不会瞬移
            _dragOffset = e.Y - _thumbY;
        }
        else
        {
            // 点在空白轨道上：把滑块中心对到这里，等价于"翻到这一页"
            _dragOffset = _thumbH / 2;
            ScrollToY(e.Y);
        }

        _dragging = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging) ScrollToY(e.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);

        if (!_dragging) return;

        _dragging = false;
        Capture = false;
        Sync();
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);

        if (_target is not { IsHandleCreated: true } box || e.Delta == 0) return;

        // 鼠标停在细条上（而不是文本框上）时也要能滚：
        // 把滚轮折算成 RichEdit 自己的行滚动，与在文本框上滚的手感一致（3 行）。
        NativeScroll.Send(box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(e.Delta > 0 ? -3 : 3));
        Sync();
    }

    /// <summary>把鼠标位置折算成滚动位置，并让 RichEdit 自己滚过去。</summary>
    private void ScrollToY(int mouseY)
    {
        if (!TryRead(out var info) || _target is not { } box) return;

        var track = Math.Max(0, Height - Inset * 2);
        var span = Math.Max(1, track - _thumbH);
        var y = Math.Clamp(mouseY - Inset - _dragOffset, 0, span);

        var range = Math.Max(1, info.nMax - info.nMin + 1);
        var page = Math.Max(1, info.nPage);
        var maxScroll = Math.Max(1, range - page);

        var pos = info.nMin + (int)Math.Round(maxScroll * (y / (double)span));

        // SB_THUMBTRACK 的 wParam 高 16 位就是目标位置 —— RichEdit 收到后自己跳过去，
        // 顺带把 ScrollInfo 更新掉。所以不能（也不需要）直接 SetScrollInfo 改位置。
        NativeScroll.Send(
            box.Handle,
            WM_VSCROLL,
            (IntPtr)(((pos & 0xFFFF) << 16) | SB_THUMBTRACK),
            IntPtr.Zero);

        Sync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Detach();
        base.Dispose(disposing);
    }

    // ---------- Win32 ----------

    [StructLayout(LayoutKind.Sequential)]
    internal struct ScrollInfo
    {
        public int cbSize;
        public int fMask;
        public int nMin;
        public int nMax;
        public int nPage;
        public int nPos;
        public int nTrackPos;
    }
}

/// <summary>
/// 滚动相关的 Win32 调用。
///
/// <para>
/// 只做两件事：把控件自己画的那根滚动条藏起来，以及读写它的滚动位置。
/// 藏滚动条是整块界面统一视觉的关键 —— <c>TextBox</c> 与 <c>RichTextBox</c> 的滚动条
/// 都是系统画的，颜色和宽度都改不了，只能隐藏后另画。
/// </para>
///
/// <para>
/// <b>隐藏不等于禁用</b>：控件的滚动范围与滚轮响应都还在，只是看不见那根条了。
/// 所以这一步不会影响任何现有交互。
/// </para>
/// </summary>
internal static class NativeScroll
{
    internal const int SbVert = 1;

    private const int GWL_STYLE = -16;
    private const long WS_VSCROLL = 0x00200000L;

    // 会让 RichEdit / Edit 重新计算并显示滚动条的消息。
    // 覆盖：重排尺寸、改文本、按键、滚轮、滚动通知、滚动到光标、改只读。
    private const int WM_SIZE = 0x0005;
    private const int WM_SETTEXT = 0x000C;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_VSCROLL = 0x0115;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int EM_REPLACESEL = 0x00C2;
    private const int EM_SCROLLCARET = 0x00B7;
    private const int EM_SETREADONLY = 0x00CF;

    [DllImport("user32.dll", EntryPoint = "ShowScrollBar")]
    private static extern bool ShowScrollBarCore(IntPtr hWnd, int wBar, bool bShow);

    [DllImport("user32.dll", EntryPoint = "GetScrollInfo")]
    private static extern bool GetScrollInfoCore(IntPtr hWnd, int fnBar, ref SlimScrollBar.ScrollInfo lpsi);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageCore(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    public static void HideVertical(IntPtr hWnd) => ShowScrollBarCore(hWnd, SbVert, false);

    /// <summary>仅供自检验证钩子用：把原生条重新显示出来，模拟 RichEdit 自己复活它的场景。</summary>
    internal static void ShowVertical(IntPtr hWnd) => ShowScrollBarCore(hWnd, SbVert, true);

    public static bool ReadVertical(IntPtr hWnd, ref SlimScrollBar.ScrollInfo info)
        => GetScrollInfoCore(hWnd, SbVert, ref info);

    public static void Send(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        => SendMessageCore(hWnd, msg, wParam, lParam);

    /// <summary>原生竖向滚动条当前是否处于"显示"状态（即窗口样式里还带着 WS_VSCROLL）。</summary>
    public static bool IsVerticalVisible(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;

        var style = IntPtr.Size == 8
            ? GetWindowLongPtr64(hWnd, GWL_STYLE).ToInt64()
            : GetWindowLong32(hWnd, GWL_STYLE);

        return (style & WS_VSCROLL) != 0;
    }

    /// <summary>
    /// 持续压制原生竖向滚动条，直到返回的对象被释放。
    ///
    /// <para>
    /// 为什么不能只调一次 <see cref="HideVertical"/>：文本框（RichEdit / Edit）在
    /// <c>WM_SIZE</c>、<c>WM_MOUSEWHEEL</c>、<c>WM_KEYDOWN</c>、<c>EM_SCROLLCARET</c>
    /// 这些消息里会自己重新计算滚动范围，然后<b>把 WS_VSCROLL 又装回窗口样式</b>。
    /// 外层按一次、它在内部再打开一次 —— 表现就是"滚一下滚轮突然冒出两根滚动条"。
    /// </para>
    ///
    /// <para>
    /// 这里用窗口子类化接管这些消息，在原生过程跑完之后检查样式，发现复活就再藏掉，
    /// 并通过 <paramref name="afterHide"/> 通知宿主重算自绘滑块的位置。
    /// 检查只是读一个窗口样式位，比每帧重画便宜得多。
    /// </para>
    /// </summary>
    public static IDisposable Suppress(Control control, Action? afterHide = null)
        => new ScrollBarSuppressor(control, afterHide);

    private static bool Watched(int msg) => msg switch
    {
        WM_SIZE or WM_SETTEXT or WM_KEYDOWN or WM_VSCROLL or WM_MOUSEWHEEL
            or EM_REPLACESEL or EM_SCROLLCARET or EM_SETREADONLY => true,
        _ => false,
    };

    /// <summary>挂在控件 HWND 上的"滚动条复活即镇压"钩子。</summary>
    private sealed class ScrollBarSuppressor : NativeWindow, IDisposable
    {
        private readonly Control _control;
        private readonly Action? _afterHide;

        /// <summary>HideVertical 本身会同步触发 WM_SIZE，不设这个标记会递归回来。</summary>
        private bool _hiding;

        private bool _disposed;

        public ScrollBarSuppressor(Control control, Action? afterHide)
        {
            _control = control;
            _afterHide = afterHide;

            control.HandleCreated += OnHandleCreated;
            control.HandleDestroyed += OnHandleDestroyed;

            if (control.IsHandleCreated) AttachTo(control.Handle);
        }

        private void OnHandleCreated(object? sender, EventArgs e)
        {
            if (_disposed || _control.IsDisposed) return;
            AttachTo(_control.Handle);
        }

        private void OnHandleDestroyed(object? sender, EventArgs e)
        {
            // 句柄没了必须松手，否则下次重建句柄时子类化会挂在已失效的 HWND 上
            if (Handle != IntPtr.Zero) ReleaseHandle();
        }

        private void AttachTo(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;

            if (Handle != IntPtr.Zero && Handle != handle) ReleaseHandle();
            if (Handle != handle) AssignHandle(handle);

            HideIfShown(handle);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);

            if (_hiding || _disposed || m.HWnd == IntPtr.Zero) return;
            if (!Watched(m.Msg)) return;

            HideIfShown(m.HWnd);
        }

        private void HideIfShown(IntPtr handle)
        {
            if (!IsVerticalVisible(handle)) return;

            _hiding = true;
            try
            {
                HideVertical(handle);
            }
            finally
            {
                _hiding = false;
            }

            // 藏掉之后客户区宽了 17px，文本会重排、滚动位置可能微调 —— 让自绘条重新读一次
            _afterHide?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            _control.HandleCreated -= OnHandleCreated;
            _control.HandleDestroyed -= OnHandleDestroyed;

            if (Handle != IntPtr.Zero) ReleaseHandle();
        }
    }
}
