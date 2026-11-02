using System.Drawing.Drawing2D;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 记录清单：左边一行时间（等宽小字），右边一行结果（着色）。
///
/// <para>
/// 「签到记录」页右下那块"最近记录"用它。用自绘而不是 RichTextBox：
/// 这里的行是对齐的两列（时间 / 变化量），RichTextBox 要把两列凑齐只能靠空格填，
/// 而中英混排下空格宽度不固定，换台机器就散了。
/// </para>
/// </summary>
internal sealed class RecordList : Control
{
    private const int PadX = 0;
    private const int RowHeight = 30;

    private (string Left, string Right, Color Tone)[] _rows = [];
    private string _empty = "还没有记录";

    public RecordList()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.CardBg;
    }

    /// <summary>空数据时显示的提示。</summary>
    public string EmptyText
    {
        get => _empty;
        set
        {
            _empty = value ?? "";
            Invalidate();
        }
    }

    /// <summary>本控件按行数需要的高度（外层拿它算卡片高度）。</summary>
    public int PreferredListHeight =>
        _rows.Length == 0 ? RowHeight + 6 : _rows.Length * RowHeight;

    public void SetRows(IEnumerable<(string Left, string Right, Color Tone)> rows)
    {
        _rows = [.. rows];
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_rows.Length == 0)
        {
            TextRenderer.DrawText(
                g,
                _empty,
                Theme.Small,
                new Rectangle(PadX, 0, Width, RowHeight),
                Theme.TextFaint,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            return;
        }

        var y = 0;

        foreach (var (left, right, tone) in _rows)
        {
            TextRenderer.DrawText(
                g,
                left,
                Theme.Mono,
                new Rectangle(PadX, y, Width / 2, RowHeight),
                Theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            TextRenderer.DrawText(
                g,
                right,
                Theme.SmallBold,
                new Rectangle(PadX + Width / 2, y, Width - Width / 2, RowHeight),
                tone,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            y += RowHeight;
        }
    }
}
