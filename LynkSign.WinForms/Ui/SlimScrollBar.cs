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

        if (box.IsHandleCreated) Sync();
    }

    public void Detach()
    {
        if (_target is not { } box) return;

        box.VScroll -= OnTargetChanged;
        box.TextChanged -= OnTargetChanged;
        box.Resize -= OnTargetChanged;
        box.HandleCreated -= OnTargetChanged;

        _target = null;
    }

    /// <summary>重新读一次滚动状态并重绘。宿主批量改完文本后可以主动调一次。</summary>
    public void Sync()
    {
        if (_target is not { IsHandleCreated: true } box) return;

        // 注意：这里不再去"藏"原生滚动条。RichEdit 会在各种消息里把它重新显示出来，
        // 拦截消息永远有漏网 —— 现在改成让宿主（ScrollHost / InputField）把原生条
        // 整个推到可见区之外裁掉，本类只负责画右边那根细条。
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

    private const int EM_SETMARGINS = 0x00D3;
    private const int EC_RIGHTMARGIN = 0x0002;

    [DllImport("user32.dll", EntryPoint = "GetScrollInfo")]
    private static extern bool GetScrollInfoCore(IntPtr hWnd, int fnBar, ref SlimScrollBar.ScrollInfo lpsi);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageCore(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public static bool ReadVertical(IntPtr hWnd, ref SlimScrollBar.ScrollInfo info)
        => GetScrollInfoCore(hWnd, SbVert, ref info);

    public static void Send(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam)
        => SendMessageCore(hWnd, msg, wParam, lParam);

    /// <summary>
    /// 给 RichEdit / Edit 设右内边距（EM_SETMARGINS）。
    ///
    /// <para>
    /// 配合"把原生滚动条推到可视区外"的用法：控件被加宽了整整一根滚动条的宽度，
    /// 文字区也随之外扩，右边那截会被下面那根自绘细条压住。设一段右内边距把文字收回来，
    /// 保证文字与细条之间始终留有缝隙。
    /// </para>
    /// </summary>
    public static void SetRightMargin(IntPtr hWnd, int pixels)
        => SendMessageCore(hWnd, EM_SETMARGINS, (IntPtr)EC_RIGHTMARGIN, (IntPtr)(pixels << 16));
}
