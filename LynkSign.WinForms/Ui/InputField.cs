using System.Diagnostics.CodeAnalysis;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 圆角输入框：外圈自己画（未聚焦淡灰、聚焦主题色 + 淡光晕），里面塞一只无边框的
/// <see cref="TextBox"/>。
///
/// <para>
/// 之所以包一层而不是去继承 TextBox 硬改 <c>WM_PAINT</c>：
/// TextBox 的原生边框与内边距（EM_SETMARGINS）在不同 Windows 主题下表现不一致，
/// 包一层之后所有外观都由自己掌控，行为则完全透传给内部 TextBox。
/// </para>
///
/// <para>
/// <see cref="Text"/> / <see cref="Lines"/> / <see cref="UseSystemPasswordChar"/> 等
/// 常用成员都做了透传，调用方不需要关心内部结构。
/// </para>
/// </summary>
internal sealed class InputField : Panel
{
    private const int PadX = 11;
    private const int PadY = 7;
    private const int SingleLineHeight = 34;
    private const int LineHeight = 19;

    private readonly TextBox _box;
    private bool _focused;

    public InputField(int lines = 1, bool password = false)
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw,
            true);

        // 圆角外的像素用卡片底色，于是输入框看起来是嵌在卡片上的
        BackColor = Theme.CardBg;

        var multiline = lines > 1;

        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = Theme.Base,
            ForeColor = Theme.TextBody,
            BackColor = Theme.CardBg,
            Multiline = multiline,
            WordWrap = multiline,
            AcceptsReturn = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            UseSystemPasswordChar = password,
        };

        _box.GotFocus += (_, _) =>
        {
            _focused = true;
            Invalidate();
        };

        _box.LostFocus += (_, _) =>
        {
            _focused = false;
            Invalidate();
        };

        Controls.Add(_box);

        Height = multiline ? lines * LineHeight + PadY * 2 + 4 : SingleLineHeight;
        Margin = new Padding(0, 3, 0, 3);
    }

    /// <summary>内部的真实 TextBox（需要挂事件或调特殊成员时用）。</summary>
    public TextBox Inner => _box;

    [AllowNull]
    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value ?? string.Empty;
    }

    public string[] Lines
    {
        get => _box.Lines;
        set => _box.Lines = value;
    }

    public bool UseSystemPasswordChar
    {
        get => _box.UseSystemPasswordChar;
        set => _box.UseSystemPasswordChar = value;
    }

    public bool ReadOnly
    {
        get => _box.ReadOnly;
        set => _box.ReadOnly = value;
    }

    public string PlaceholderText
    {
        get => _box.PlaceholderText;
        set => _box.PlaceholderText = value;
    }

    public bool Multiline => _box.Multiline;

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);

        var inner = new Rectangle(
            PadX,
            PadY,
            Math.Max(0, Width - PadX * 2),
            Math.Max(0, Height - PadY * 2));

        // 单行 TextBox 的高度由字体决定、改不了，所以要手动垂直居中，
        // 否则它会贴着单元格顶部，看起来像没对齐。
        if (_box.Multiline)
        {
            _box.Bounds = inner;
        }
        else
        {
            var h = Math.Min(_box.PreferredHeight, inner.Height);
            _box.Bounds = new Rectangle(inner.X, inner.Y + (inner.Height - h) / 2, inner.Width, h);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        var r = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);

        Theme.FillRound(g, r, Theme.FieldRadius, Theme.CardBg);

        if (_focused)
        {
            // 聚焦：先用淡色描一圈稍粗的"光晕"，再压一条实色边，看起来更柔和
            Theme.DrawRound(g, RectangleF.Inflate(r, 1.5F, 1.5F), Theme.FieldRadius + 1.5F, Theme.AccentSoft, 2F);
            Theme.DrawRound(g, r, Theme.FieldRadius, Theme.Accent);
        }
        else
        {
            Theme.DrawRound(g, r, Theme.FieldRadius, Color.FromArgb(0xD8, 0xDD, 0xE6));
        }
    }
}
