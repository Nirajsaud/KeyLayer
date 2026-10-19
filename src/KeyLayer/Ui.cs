namespace KeyLayer;

/// <summary>Shared look: fonts, colors and a few control factories.</summary>
internal static class Ui
{
    public static readonly Font BaseFont = new("Segoe UI", 9.75f);
    public static readonly Font StrongFont = new("Segoe UI Semibold", 9.75f);
    public static readonly Font HeadingFont = new("Segoe UI Semibold", 11.25f);
    public static readonly Font TitleFont = new("Segoe UI Semibold", 16f);

    public static readonly Color Accent = Color.FromArgb(37, 99, 235);
    public static readonly Color AccentHover = Color.FromArgb(29, 78, 216);
    public static readonly Color AccentDisabled = Color.FromArgb(191, 210, 250);
    public static readonly Color Ink = Color.FromArgb(17, 24, 39);
    public static readonly Color Muted = Color.FromArgb(100, 108, 122);
    public static readonly Color Warning = Color.FromArgb(180, 83, 9);
    public static readonly Color WarningBack = Color.FromArgb(255, 247, 237);
    public static readonly Color Subtle = Color.FromArgb(243, 244, 246);
    public static readonly Color HeaderBack = Color.FromArgb(17, 24, 39);
    public static readonly Color HeaderMuted = Color.FromArgb(156, 163, 175);
    public static readonly Color HeaderChip = Color.FromArgb(31, 41, 55);
    public static readonly Color Good = Color.FromArgb(74, 222, 128);

    public static Button MakeButton(string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Size = new Size(170, 36),
            Margin = new Padding(0, 0, 0, 8),
            UseVisualStyleBackColor = !primary,
        };
        if (primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.FlatAppearance.MouseOverBackColor = AccentHover;
            button.FlatAppearance.MouseDownBackColor = AccentHover;
            button.BackColor = Accent;
            button.ForeColor = Color.White;
            button.Font = StrongFont;
            button.EnabledChanged += (_, _) => button.BackColor = button.Enabled ? Accent : AccentDisabled;
        }
        return button;
    }

    public static Label MakeLabel(string text, Color? color = null, Font? font = null) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color ?? Ink,
        Font = font ?? BaseFont,
        Margin = new Padding(0, 0, 0, 4),
    };

    /// <summary>Adds a column whose share of the list's width is proportional to <paramref name="weight"/>.</summary>
    public static void AddColumn(ListView list, string text, int weight)
    {
        if (list.Columns.Count == 0) list.SizeChanged += (_, _) => FitColumns(list);
        list.Columns.Add(new ColumnHeader { Text = text, Width = weight, Tag = weight });
    }

    /// <summary>Spreads the columns across the visible width, so there's never a horizontal scrollbar.</summary>
    public static void FitColumns(ListView list)
    {
        int available = list.ClientSize.Width - 4;
        int total = list.Columns.Cast<ColumnHeader>().Sum(c => c.Tag as int? ?? 0);
        if (available <= 0 || total <= 0) return;
        int used = 0;
        for (int i = 0; i < list.Columns.Count; i++)
        {
            var column = list.Columns[i];
            int width = i == list.Columns.Count - 1 ? available - used : available * (int)column.Tag! / total;
            column.Width = width;
            used += width;
        }
    }

    /// <summary>Row height isn't DPI-scaled by WinForms; a 1-pixel-wide image list sets it.</summary>
    public static void ScaleList(ListView list)
    {
        list.SmallImageList = new ImageList { ImageSize = new Size(1, list.LogicalToDeviceUnits(26)) };
        FitColumns(list);
    }

    /// <summary>Asks for one line of text. Returns null if cancelled.</summary>
    public static string? Prompt(IWin32Window owner, string title, string message, string initial)
    {
        using var form = new Form
        {
            Text = title,
            Font = BaseFont,
            BackColor = Color.White,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.CenterParent,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
        };
        form.SuspendLayout();

        var root = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Padding = new Padding(20, 16, 20, 14) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 400));
        var label = MakeLabel(message, Muted);
        label.MaximumSize = new Size(400, 0);
        var box = new TextBox { Text = initial, Dock = DockStyle.Fill, Margin = new Padding(0, 6, 0, 0) };

        var save = MakeButton("Save", primary: true);
        var cancel = MakeButton("Cancel");
        save.Size = cancel.Size = new Size(100, 32);
        save.Margin = new Padding(10, 0, 0, 0);
        cancel.Margin = Padding.Empty;
        save.DialogResult = DialogResult.OK;
        cancel.DialogResult = DialogResult.Cancel;
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 16, 0, 0) };
        row.Controls.Add(save);
        row.Controls.Add(cancel);

        root.Controls.Add(label);
        root.Controls.Add(box);
        root.Controls.Add(row);
        form.Controls.Add(root);
        form.AcceptButton = save;
        form.CancelButton = cancel;
        form.AutoScaleDimensions = new SizeF(96F, 96F);
        form.AutoScaleMode = AutoScaleMode.Dpi;
        form.ResumeLayout(false);
        form.PerformLayout();

        return form.ShowDialog(owner) == DialogResult.OK ? box.Text : null;
    }
}

internal static class AppIcon
{
    static Stream Open() =>
        typeof(AppIcon).Assembly.GetManifestResourceStream("app.ico") ?? throw new InvalidOperationException("Icon resource missing.");

    public static Icon Window()
    {
        using var s = Open();
        return new Icon(s);
    }

    public static Icon Tray()
    {
        using var s = Open();
        return new Icon(s, SystemInformation.SmallIconSize);
    }
}
