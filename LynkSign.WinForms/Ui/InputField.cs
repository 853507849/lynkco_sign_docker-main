using System.Diagnostics.CodeAnalysis;

namespace LynkSign.WinForms.Ui;

/// <summary>
/// 圆角输入框：平时是一块浅灰的"填充块"，聚焦时才变成白底 + 主题色描边。
///
/// <para>
/// 之所以包一层而不是去继承 TextBox 硬改 <c>WM_PAINT</c>：
/// TextBox 的原生边框与内边距（EM_SETMARGINS）在不同 Windows 主题下表现不一致，
/// 包一层之后所有外观都由自己掌控，行为则完全透传给内部 TextBox。
/// </para>
///
/// <para>
/// 表单里一屏能有十几个输入框。每个都描一圈灰边的话，整张卡片会被网格线切碎；
/// 换成"底色区分 + 只有聚焦的那个才描边"，视线自然落在正在填的那一格上。
/// </para>
///
/// <para>
/// <see cref="Text"/> / <see cref="Lines"/> / <see cref="UseSystemPasswordChar"/> 等
/// 常用成员都做了透传，调用方不需要关心内部结构。
/// </para>
/// </summary>
internal sealed class InputField : Panel
{
    private const int PadX = 14;
    private const int PadY = 10;
    private const int LineHeight = 19;

    private readonly TextBox _box;
    private readonly Panel? _clip;
    private readonly int _preferredHeight;
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
            BackColor = Theme.FieldBg,
            Multiline = multiline,
            WordWrap = multiline,
            AcceptsReturn = multiline,
            ScrollBars = multiline ? ScrollBars.Vertical : ScrollBars.None,
            UseSystemPasswordChar = password,
        };

        _box.GotFocus += (_, _) =>
        {
            _focused = true;
            _box.BackColor = Theme.CardBg;
            if (_clip is not null) _clip.BackColor = Theme.CardBg;
            Invalidate();
        };

        _box.LostFocus += (_, _) =>
        {
            _focused = false;
            _box.BackColor = Theme.FieldBg;
            if (_clip is not null) _clip.BackColor = Theme.FieldBg;
            Invalidate();
        };

        if (multiline)
        {
            // 多行 TextBox 会自己画一根滚动条（白底 + 上下箭头），压在浅灰的填充块里
            // 比输入框本身还显眼。
            //
            // 不去"藏"它（Edit 控件在滚轮/按键/改尺寸后会把 WS_VSCROLL 装回去，
            // 藏一次的后果就是滚两下白条又冒出来，变成两根）。这里垫一层承载框，
            // 把文本框加宽整整一根滚动条，多出来的那截被承载框裁在可视区之外。
            _clip = new Panel
            {
                BackColor = Theme.FieldBg,
                Margin = Padding.Empty,
            };
            _clip.Controls.Add(_box);
            Controls.Add(_clip);
        }
        else
        {
            Controls.Add(_box);
        }

        // 单行高度按内部 TextBox 的真实高度算，不能写死一个 36：
        // 150% 缩放下 9pt 的一行要 23px，留给它的空间少 1px 都会被裁掉半行字
        // （实测写字位是 20px 时占位提示直接一个字都看不见，只剩一个空灰条）。
        Height = multiline ? lines * LineHeight + PadY * 2 + 6 : PadY * 2 + _box.PreferredHeight;
        _preferredHeight = Height;
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

    /// <summary>
    /// 高度就是构造时定的那个值，不再让基类按"内部 TextBox 的期望尺寸"另算一个。
    ///
    /// <para>
    /// 不覆写的话，Panel 的默认实现会报出一个比实际更高的首选高度（实测 61 vs 36），
    /// 而表单行是按子控件的首选高度自适应（AutoSize 行）——于是同一张卡里
    /// "带说明文字的那一行"的输入框会比别的行高出一截，视觉上像两种控件。
    /// </para>
    ///
    /// <para>
    /// 必须用构造时的值，不能用当前的 <see cref="Control.Height"/>：控件被 Dock=Fill
    /// 拉高之后，再按 Height 报首选高度就成了正反馈 —— 每排一次版就再高一圈。
    /// </para>
    /// </summary>
    public override Size GetPreferredSize(Size proposedSize) => new(0, _preferredHeight);

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);

        var inner = new Rectangle(
            PadX,
            PadY,
            Math.Max(0, Width - PadX * 2),
            Math.Max(0, Height - PadY * 2));

        if (_clip is not null)
        {
            // 多行：承载框就是文字的可见区；文本框加宽一根原生滚动条，
            // 多出来的那截（原生条）落在承载框之外，被它裁掉，界面上只剩自己画的那根。
            _clip.Bounds = inner;

            _box.Bounds = new Rectangle(0, 0, inner.Width, inner.Height);

            var pad = _box.IsHandleCreated ? Math.Max(0, _box.Width - _box.ClientSize.Width) : 0;
            if (pad <= 0) pad = SystemInformation.VerticalScrollBarWidth;

            _box.Bounds = new Rectangle(0, 0, inner.Width + pad, inner.Height);
            return;
        }

        // 单行 TextBox 的高度由字体决定、改不了，所以要手动垂直居中，
        // 否则它会贴着单元格顶部，看起来像没对齐。
        var h = Math.Min(_box.PreferredHeight, inner.Height);
        _box.Bounds = new Rectangle(inner.X, inner.Y + (inner.Height - h) / 2, inner.Width, h);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;

        Theme.ClearBackdrop(g, this);

        var r = new RectangleF(0.5F, 0.5F, Width - 1F, Height - 1F);

        if (_focused)
        {
            Theme.FillRound(g, r, Theme.FieldRadius, Theme.CardBg);

            // 聚焦：先用一圈极淡的外扩做光晕，再压一条实色边，看起来更柔和
            Theme.DrawRound(g, RectangleF.Inflate(r, 1.5F, 1.5F), Theme.FieldRadius + 1.5F, Theme.AccentSoft, 2F);
            Theme.DrawRound(g, r, Theme.FieldRadius, Theme.Accent, 1.4F);
        }
        else
        {
            Theme.FillRound(g, r, Theme.FieldRadius, Theme.FieldBg);
        }
    }
}
