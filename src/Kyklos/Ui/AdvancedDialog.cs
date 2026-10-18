using Kyklos.Core;

namespace Kyklos.Ui;

/// <summary>Trim / resize / quality editor, opened from the wheel's "Trim/Size" tool.</summary>
internal sealed class AdvancedDialog : Form
{
    private readonly TextBox _start;
    private readonly TextBox _end;
    private readonly TextBox _width;
    private readonly ComboBox _quality;

    private AdvancedDialog(ConvertOptions current, MediaKind kind)
    {
        Text = "Trim / Size / Quality";
        Dark.Apply(this);
        Size = new Size(430, kind is MediaKind.Image ? 300 : 360);

        var supportsTrim = kind is MediaKind.Video or MediaKind.Audio;
        var supportsResize = kind is MediaKind.Video or MediaKind.Image;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            Padding = new Padding(18, 16, 18, 12),
            BackColor = Dark.Back,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        layout.Controls.Add(Dark.Label("Quality", bold: true), 0, 0);
        _quality = Dark.Combo(["balanced", "fast", "best"], current.Quality);
        layout.Controls.Add(_quality, 1, 0);
        layout.SetColumnSpan(_quality, 1);

        var row = 1;

        layout.Controls.Add(Dark.Label("Trim start", bold: true), 0, row);
        _start = Dark.Field(Format(current.TrimStart));
        _start.Enabled = supportsTrim;
        layout.Controls.Add(_start, 1, row++);

        layout.Controls.Add(Dark.Label("Trim end", bold: true), 0, row);
        _end = Dark.Field(Format(current.TrimEnd));
        _end.Enabled = supportsTrim;
        layout.Controls.Add(_end, 1, row++);

        layout.Controls.Add(Dark.Label("Resize width", bold: true), 0, row);
        _width = Dark.Field(current.ResizeWidth?.ToString() ?? string.Empty);
        _width.Enabled = supportsResize;
        layout.Controls.Add(_width, 1, row++);

        var hint = Dark.Caption(supportsTrim
            ? "Times accept ss, mm:ss or hh:mm:ss. Leave blank for no trim. Width blank keeps the original size."
            : "Width blank keeps the original size.");
        layout.Controls.Add(hint, 0, row);
        layout.SetColumnSpan(hint, 2);
        row++;

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            BackColor = Color.Transparent,
            AutoSize = true,
        };
        var ok = Dark.Primary("Apply");
        ok.Click += (_, _) => { Apply(current); };
        var cancel = Dark.Secondary("Cancel");
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);

        layout.Controls.Add(buttons, 0, row);
        layout.SetColumnSpan(buttons, 2);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        Result = current;
    }

    public ConvertOptions Result { get; private set; }

    public static ConvertOptions? Edit(IWin32Window owner, ConvertOptions current, MediaKind kind)
    {
        using var dialog = new AdvancedDialog(current.Clone(), kind);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.Result : null;
    }

    private void Apply(ConvertOptions current)
    {
        var updated = current.Clone();
        updated.Quality = _quality.SelectedItem?.ToString() ?? Quality.Balanced;
        updated.TrimStart = _start.Enabled ? Parse(_start.Text) : null;
        updated.TrimEnd = _end.Enabled ? Parse(_end.Text) : null;

        if (_width.Enabled && int.TryParse(_width.Text.Trim(), out var width) && width >= 16)
            updated.ResizeWidth = width;
        else
            updated.ResizeWidth = null;

        if (updated.TrimStart is { } start && updated.TrimEnd is { } end && end <= start)
        {
            MessageBox.Show(this, "Trim end must be after trim start.", "Kyklos",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        Result = updated;
        DialogResult = DialogResult.OK;
        Close();
    }

    private static string Format(TimeSpan? value)
        => value is null ? string.Empty : value.Value.ToString(value.Value.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss");

    private static TimeSpan? Parse(string text)
    {
        text = text.Trim();
        if (text.Length == 0) return null;

        var parts = text.Split(':');
        if (parts.Length > 3) return null;

        double seconds = 0;
        foreach (var part in parts)
        {
            if (!double.TryParse(part, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
                return null;
            seconds = seconds * 60 + value;
        }

        return seconds <= 0 ? null : TimeSpan.FromSeconds(seconds);
    }
}
