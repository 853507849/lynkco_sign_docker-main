using System.Globalization;
using System.Text.RegularExpressions;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 「账户」深色卡：积分余额 + 成长进度 + 累计获得 + 凭据尾号。
///
/// <para>
/// 整块自己画，而不是往 <see cref="CardPanel"/> 里塞一堆 Label：这张卡的数字和说明
/// 是同一套基线关系（大数字下面的细进度条、右侧对齐的累计值），用 Label + 布局容器拼
/// 很难在换字号/换 DPI 后仍然对齐，而自绘只用一次排版计算。
/// </para>
///
/// <para>
/// <b>块的排布是"预算式"的</b>：按内容算好每一块的高度、从上往下画，剩余空间不够就整块跳过
/// （而不是硬画上去让它压住上一块）。卡片高度随窗口变化，早先那版把凭据块钉在底部、
/// 其余按固定顺序摆，窗口一矮就出现「成长值」与凭据块叠在一起 ——
/// 这类重叠不会报错，只能靠眼睛发现。
/// </para>
/// </summary>
internal sealed class AccountCard : Control
{
    private const int PadX = 18;
    private const int PadY = 16;

    private string _point = "—";
    private string _income = "—";
    private string _level = "—";
    private string _growth = "—";
    private string _tokenTail = "";
    private string _deviceId = "";

    public AccountCard()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        BackColor = Theme.PageBg;
    }

    /// <summary>卡片标题右侧的小字。</summary>
    public string Note { get; set; } = "只读缓存";

    public string Point
    {
        get => _point;
        set => Set(ref _point, value);
    }

    public string Income
    {
        get => _income;
        set => Set(ref _income, value);
    }

    public string Level
    {
        get => _level;
        set => Set(ref _level, value);
    }

    public string Growth
    {
        get => _growth;
        set => Set(ref _growth, value);
    }

    /// <summary>refreshToken 尾号（只显示后 4 位，其余一律不露）。</summary>
    public string TokenTail
    {
        get => _tokenTail;
        set => Set(ref _tokenTail, value);
    }

    public string DeviceId
    {
        get => _deviceId;
        set => Set(ref _deviceId, value);
    }

    private void Set(ref string field, string? value)
    {
        var v = string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        if (field == v) return;

        field = v;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this, Theme.PageBg);

        var card = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);
        Theme.DrawCardFilled(g, card, Theme.CardRadius, Theme.RailBg, Theme.DarkBorder);

        var left = PadX;
        var right = Width - PadX;
        var bottom = Height - PadY;
        var y = PadY;

        // ---- 标题行 ----
        TextRenderer.DrawText(g, "账户", Theme.Section, new Point(left, y), Theme.OnDark, TextFormatFlags.NoPadding);
        TextRenderer.DrawText(
            g, Note, Theme.Small, new Rectangle(left, y, Width - PadX * 2, Theme.Section.Height),
            Theme.OnDarkFaint,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

        y += Theme.Section.Height + 12;

        // ---- 顶部信息块（积分余额 + 成长进度 + 两行明细）----
        // 先算这一块整体要占多高，再决定凭据块画不画得下：
        // 反过来（先钉住凭据块、剩下的都给上面）会让上面在窗口偏矮时压下来盖住它。
        var (growth, need) = ParseGrowth(_growth);
        var hasBar = need > 0 && growth > 0;

        var captionH = Theme.Small.Height;
        var pointH = Theme.Display.Height;
        var rowH = Theme.Small.Height + 12;
        var barH = hasBar ? 22 : 0;                       // 进度条 + 它与文字之间的空隙

        var infoH = captionH + 4 + pointH + 6 + barH + rowH + rowH;

        const int boxH = 56;
        var showBox = bottom - boxH > y + infoH;
        var boxTop = bottom - boxH;

        if (!showBox)
        {
            // 空间不够：优先保证上面的数字读得全，凭据块整块不画（宁缺勿叠）。
            infoH = Math.Max(0, bottom - y);
        }
        else if (y + infoH > boxTop - 12)
        {
            // 挤得下但不宽裕：把两行明细的间距收一收（不是简单裁掉内容）。
            rowH = Math.Max(Theme.Small.Height + 4, (boxTop - 12 - y - captionH - 4 - pointH - 6 - barH) / 2);
            infoH = captionH + 4 + pointH + 6 + barH + rowH * 2;
        }

        // ---- 积分余额 ----
        TextRenderer.DrawText(g, "积分余额", Theme.Small, new Point(left, y), Theme.OnDarkMuted, TextFormatFlags.NoPadding);
        y += captionH + 4;

        TextRenderer.DrawText(g, _point, Theme.Display, new Point(left, y - 6), Theme.OnDark, TextFormatFlags.NoPadding);
        y += pointH + 6;

        // ---- 成长进度条：细线，右端对齐数值 ----
        if (hasBar)
        {
            var ratio = Math.Clamp((float)growth / need, 0F, 1F);
            var track = new RectangleF(left, y + 3, Math.Max(20, right - left), 5F);

            Theme.FillRound(g, track, 2.5F, Color.FromArgb(0x22, 0x2C, 0x38));
            if (ratio > 0.01F)
            {
                Theme.FillRound(g, new RectangleF(track.X, track.Y, Math.Max(5F, track.Width * ratio), track.Height),
                    2.5F, Theme.AccentLight);
            }

            y += barH;
        }

        // ---- 累计获得 / 成长等级（成长值并进等级那一行：进度条已经说明它在哪一档）----
        // 接口只给了成长值、没给门槛时，进度条画不出来（没有分母），
        // 但数值本身是有效信息，就并进等级那一行，不能因为画不了条就丢掉。
        var levelText = need > 0 && growth > 0
            ? $"{_level} · {growth} / {need}"
            : growth > 0
                ? $"{_level} · 成长值 {growth}"
                : _level;

        Row(g, ref y, rowH, "累计获得", _income, Theme.OnDark);
        Row(g, ref y, rowH, "成长等级", levelText, Theme.OnDark);

        // ---- 底部凭据块 ----
        if (!showBox) return;

        var box = new RectangleF(left, boxTop, right - left, boxH);

        Theme.FillRound(g, box, 9F, Color.FromArgb(0x16, 0x1F, 0x29));
        Theme.DrawRound(g, box, 9F, Color.FromArgb(0x22, 0x2C, 0x38));

        var line1 = string.IsNullOrEmpty(_tokenTail) ? "尚未绑定 refreshToken" : $"refreshToken 尾号 {_tokenTail}";
        var line2 = string.IsNullOrEmpty(_deviceId) ? "—" : "deviceId " + Ellipsis(_deviceId, 30);

        TextRenderer.DrawText(
            g, line1, Theme.Small, new Point((int)box.X + 14, (int)box.Y + 11),
            Theme.OnDarkMuted, TextFormatFlags.NoPadding);

        TextRenderer.DrawText(
            g, line2, Theme.MonoSmall,
            new Point((int)box.X + 14, (int)box.Y + 11 + Theme.Small.Height + 8),
            Theme.OnDarkFaint, TextFormatFlags.NoPadding);
    }

    /// <summary>一行「左标签 + 右数值」，标签灰、数值亮，基线对齐。</summary>
    private void Row(Graphics g, ref int y, int rowH, string caption, string value, Color color)
    {
        // 右对齐靠"矩形右边界"实现，所以宽度要减掉右内边距，否则数值会顶到卡片边上。
        var box = new Rectangle(0, y, Width - PadX, Theme.Small.Height + 2);

        TextRenderer.DrawText(g, caption, Theme.Small, new Point(PadX, y), Theme.OnDarkMuted,
            TextFormatFlags.NoPadding);

        TextRenderer.DrawText(g, value, Theme.Mono, box, color,
            TextFormatFlags.Right | TextFormatFlags.NoPadding);

        y += rowH;
    }

    /// <summary>
    /// 把「2919 / 3000」这种成长值拆成两个数。
    /// 接口给的可能是纯数字，也可能已经带了门槛；解析不出来就返回 (0, 0)，进度条就不画。
    /// </summary>
    private static (int Growth, int Need) ParseGrowth(string raw)
    {
        var match = Regex.Match(raw, @"(\d[\d,]*)\s*/\s*(\d[\d,]*)");
        if (match.Success
            && int.TryParse(match.Groups[1].Value.Replace(",", ""), out var growth)
            && int.TryParse(match.Groups[2].Value.Replace(",", ""), out var need))
        {
            return (growth, need);
        }

        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var only)
            ? (only, 0)
            : (0, 0);
    }

    private static string Ellipsis(string text, int max)
        => text.Length <= max ? text : text[..(max - 1)] + "…";
}
