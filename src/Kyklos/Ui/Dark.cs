namespace Kyklos.Ui;

/// <summary>Small factory for the dark control styling used by the settings and tool dialogs.</summary>
internal static class Dark
{
    public static readonly Color Back = Color.FromArgb(24, 24, 29);
    public static readonly Color Panel = Color.FromArgb(33, 33, 40);
    public static readonly Color FieldBack = Color.FromArgb(44, 44, 53);
    public static readonly Color Border = Color.FromArgb(66, 66, 78);
    public static readonly Color Text = Color.FromArgb(240, 240, 246);
    public static readonly Color Muted = Color.FromArgb(158, 160, 174);
    public static readonly Color Accent = Theme.Accent;

    public static Label Label(string text, bool bold = false, Color? color = null) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = color ?? Text,
        BackColor = Color.Transparent,
        Font = Theme.Font(12.5f, bold ? FontStyle.Bold : FontStyle.Regular),
        Margin = new Padding(0, 4, 0, 2),
    };

    public static Label Caption(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Muted,
        BackColor = Color.Transparent,
        Font = Theme.Font(11.5f),
        Margin = new Padding(0, 0, 0, 6),
    };

    public static TextBox Field(string value) => new()
    {
        Text = value,
        BackColor = FieldBack,
        ForeColor = Text,
        BorderStyle = BorderStyle.FixedSingle,
        Font = Theme.Font(12.5f),
        Width = 320,
    };

    public static ComboBox Combo(IEnumerable<string> items, string? selected) 
    {
        var combo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            BackColor = FieldBack,
            ForeColor = Text,
            FlatStyle = FlatStyle.Flat,
            Font = Theme.Font(12.5f),
            Width = 240,
        };
        foreach (var item in items) combo.Items.Add(item);
        if (selected is not null && combo.Items.Contains(selected)) combo.SelectedItem = selected;
        else if (combo.Items.Count > 0) combo.SelectedIndex = 0;
        return combo;
    }

    public static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        ForeColor = Text,
        BackColor = Color.Transparent,
        FlatStyle = FlatStyle.Flat,
        Font = Theme.Font(12.5f),
        Margin = new Padding(0, 6, 0, 2),
    };

    public static Button Primary(string text)
    {
        var button = Secondary(text);
        button.BackColor = Accent;
        button.ForeColor = Color.FromArgb(28, 16, 6);
        button.FlatAppearance.MouseOverBackColor = Theme.AccentSoft;
        button.Font = Theme.Font(12.5f, FontStyle.Bold);
        return button;
    }

    public static Button Secondary(string text)
    {
        var button = new Button
        {
            Text = text,
            AutoSize = false,
            Height = 30,
            Width = 108,
            FlatStyle = FlatStyle.Flat,
            BackColor = Panel,
            ForeColor = Text,
            Font = Theme.Font(12.5f),
            Margin = new Padding(6, 4, 0, 4),
        };
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = FieldBack;
        return button;
    }

    public static void Apply(Form form)
    {
        form.BackColor = Back;
        form.ForeColor = Text;
        form.Font = Theme.Font(12.5f);
        form.FormBorderStyle = FormBorderStyle.FixedDialog;
        form.MaximizeBox = false;
        form.MinimizeBox = false;
        form.StartPosition = FormStartPosition.CenterScreen;
        form.ShowInTaskbar = true;
    }
}
