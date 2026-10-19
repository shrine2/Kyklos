using System.Diagnostics;
using Kyklos.Core;
using Kyklos.Interop;

namespace Kyklos.Ui;

/// <summary>All user-facing configuration: tool paths, output rules, wheel trigger and shell integration.</summary>
internal sealed class SettingsForm : Form
{
    private static readonly (string Label, string Value)[] OverwriteChoices =
    [
        ("Keep both (rename)", OverwriteModes.Rename),
        ("Overwrite the existing file", OverwriteModes.Overwrite),
        ("Skip if it already exists", OverwriteModes.Skip),
    ];

    private static readonly string[] TriggerChoices = ["Ctrl+Alt", "Ctrl+Shift", "Alt+Shift", "Shift", "Alt"];

    private readonly Settings _settings;

    private readonly TextBox _ffmpegPath;
    private readonly TextBox _sofficePath;
    private readonly Label _ffmpegStatus;
    private readonly Label _sofficeStatus;
    private readonly RadioButton _beside;
    private readonly RadioButton _subfolder;
    private readonly TextBox _subfolderName;
    private readonly ComboBox _overwrite;
    private readonly ComboBox _quality;
    private readonly CheckBox _enableSurface;
    private readonly ComboBox _trigger;
    private readonly ComboBox _hotkey;
    private readonly CheckBox _showDropTarget;
    private readonly CheckBox _startup;
    private readonly Button _sendToButton;

    public Settings Result { get; }

    public static Settings? Edit(IWin32Window owner, Settings current)
    {
        using var form = new SettingsForm(current.Clone());
        return form.ShowDialog(owner) == DialogResult.OK ? form.Result : null;
    }

    private SettingsForm(Settings settings)
    {
        _settings = settings;
        Result = settings;

        Text = "Kyklos — settings";
        Dark.Apply(this);
        Size = new Size(640, 720);
        MinimumSize = new Size(560, 520);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _ffmpegStatus = Dark.Caption(string.Empty);
        _sofficeStatus = Dark.Caption(string.Empty);
        _ffmpegPath = Dark.Field(settings.FfmpegPath ?? string.Empty);
        _sofficePath = Dark.Field(settings.LibreOfficePath ?? string.Empty);

        _beside = Radio("Save next to the original", settings.OutputMode == OutputModes.Beside);
        _subfolder = Radio("Save into a subfolder named", settings.OutputMode == OutputModes.Subfolder);
        _subfolderName = Dark.Field(settings.SubfolderName);
        _subfolderName.Width = 160;
        _subfolderName.Enabled = settings.OutputMode == OutputModes.Subfolder;
        _beside.CheckedChanged += (_, _) => _subfolderName.Enabled = _subfolder.Checked;

        _overwrite = Dark.Combo(OverwriteChoices.Select(c => c.Label), LabelFor(settings.OverwriteMode));
        _overwrite.Width = 260;

        _quality = Dark.Combo([Quality.Best, Quality.Balanced, Quality.Fast], Quality.Normalize(settings.Quality));
        _quality.Width = 160;

        _enableSurface = Dark.Check("Hold the trigger keys and drag in Explorer to open the wheel", settings.EnableDragSurface);
        _trigger = Dark.Combo(TriggerChoices, settings.DragModifiers);
        _trigger.Width = 160;
        _hotkey = Dark.Combo(Hotkeys.Presets, settings.Hotkey);
        _hotkey.Width = 160;
        _hotkey.DropDownStyle = ComboBoxStyle.DropDown;
        _showDropTarget = Dark.Check("Show the floating drop target on the desktop", settings.ShowDropTarget);
        // Read the registry rather than the stored flag: they can disagree if the write was
        // blocked by policy, and the checkbox should show what Windows will actually do.
        _startup = Dark.Check("Start Kyklos when Windows starts", ShellIntegration.IsStartupEnabled());
        _sendToButton = Dark.Secondary(ShellIntegration.SendToInstalled ? "Remove" : "Install");
        _sendToButton.Width = 130;
        _sendToButton.Click += (_, _) => ToggleSendTo();

        BuildLayout();
        RefreshToolStatus();
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Dark.Back,
            Padding = new Padding(18, 12, 18, 12),
        };

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            BackColor = Dark.Back,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var row = 0;

        void Section(string title)
        {
            var label = Dark.Label(title, bold: true, color: Theme.AccentSoft);
            label.Margin = new Padding(0, 16, 0, 2);
            layout.Controls.Add(label, 0, row);
            layout.SetColumnSpan(label, 2);
            row++;
        }

        void Field(string label, Control control, Control? extra = null)
        {
            var caption = Dark.Label(label);
            caption.Margin = new Padding(0, 9, 8, 0);
            layout.Controls.Add(caption, 0, row);

            if (extra is null)
            {
                control.Margin = new Padding(0, 6, 0, 0);
                layout.Controls.Add(control, 1, row);
            }
            else
            {
                var strip = new FlowLayoutPanel
                {
                    FlowDirection = FlowDirection.LeftToRight,
                    AutoSize = true,
                    BackColor = Color.Transparent,
                    Margin = new Padding(0, 4, 0, 0),
                    WrapContents = false,
                };
                control.Margin = new Padding(0, 2, 6, 0);
                extra.Margin = new Padding(0, 2, 0, 0);
                strip.Controls.Add(control);
                strip.Controls.Add(extra);
                layout.Controls.Add(strip, 1, row);
            }
            row++;
        }

        void Note(string text)
        {
            var caption = Dark.Caption(text);
            caption.MaximumSize = new Size(400, 0);
            caption.Margin = new Padding(0, 0, 0, 4);
            layout.Controls.Add(caption, 1, row);
            row++;
        }

        Section("External tools");

        var ffmpegBrowse = Dark.Secondary("Browse");
        ffmpegBrowse.Click += (_, _) => Browse(_ffmpegPath, "ffmpeg.exe");
        Field("ffmpeg.exe", _ffmpegPath, ffmpegBrowse);

        var rescan = Dark.Secondary("Re-scan");
        rescan.Click += (_, _) => RescanTools();
        layout.Controls.Add(rescan, 1, row);
        row++;

        _ffmpegStatus.MaximumSize = new Size(400, 0);
        layout.Controls.Add(_ffmpegStatus, 1, row);
        row++;

        var sofficeBrowse = Dark.Secondary("Browse");
        sofficeBrowse.Click += (_, _) => Browse(_sofficePath, "soffice.exe");
        Field("soffice.exe (LibreOffice)", _sofficePath, sofficeBrowse);

        var install = Dark.Secondary("Install");
        install.Click += (_, _) => InstallLibreOffice();
        layout.Controls.Add(install, 1, row);
        row++;

        _sofficeStatus.MaximumSize = new Size(400, 0);
        layout.Controls.Add(_sofficeStatus, 1, row);
        row++;
        Note("LibreOffice unlocks the Word / EPUB / Pages / spreadsheet conversions. Everything else works without it.");

        Section("Output");
        Field("Save to", _beside);
        Field(string.Empty, _subfolder, _subfolderName);
        Field("If the file exists", _overwrite);
        Field("Compression presets", _quality);
        Note("Quality presets control the wheel's Compress / No-meta tools and the default bitrate of audio and video targets.");

        Section("Interaction");
        Field("Drag trigger", _trigger);
        layout.Controls.Add(_enableSurface, 1, row);
        row++;
        Field("Global hotkey", _hotkey);
        layout.Controls.Add(_showDropTarget, 1, row);
        row++;
        layout.Controls.Add(_startup, 1, row);
        row++;
        Note("The wheel only arms when the trigger keys are held while a pointer drag starts over an Explorer file view, " +
             "so ordinary clicks and drags inside other applications are never intercepted. Shift alone conflicts with " +
             "Explorer's move-on-drop, which is why Ctrl+Alt is the default.");

        Section("Explorer integration");
        Field("Explorer Send to", Dark.Label(ShellIntegration.SendToInstalled ? "Convert with Kyklos (installed)" : "not installed"), _sendToButton);

        scroll.Controls.Add(layout);

        var footer = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Dark.Back };
        var save = Dark.Primary("Save");
        save.Width = 120;
        save.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        save.Location = new Point(footer.Width - 140, 12);
        save.Click += (_, _) => Save();
        var cancel = Dark.Secondary("Cancel");
        cancel.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        cancel.Location = new Point(footer.Width - 260, 12);
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        footer.Controls.Add(save);
        footer.Controls.Add(cancel);
        footer.Resize += (_, _) =>
        {
            save.Location = new Point(footer.Width - save.Width - 20, 12);
            cancel.Location = new Point(save.Left - cancel.Width - 8, 12);
        };

        Controls.Add(scroll);
        Controls.Add(footer);
        AcceptButton = save;
    }

    private static RadioButton Radio(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        ForeColor = Dark.Text,
        BackColor = Color.Transparent,
        FlatStyle = FlatStyle.Flat,
        Font = Theme.Font(12.5f),
        Margin = new Padding(0, 6, 0, 0),
    };

    // ---------------------------------------------------------------- actions

    private void Browse(TextBox target, string filter)
    {
        using var dialog = new OpenFileDialog
        {
            Filter = $"{filter}|{filter}|All files|*.*",
            Title = $"Locate {filter}",
        };

        var current = target.Text.Trim();
        if (current.Length > 0)
        {
            var directory = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
            if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory)) dialog.InitialDirectory = directory;
        }

        if (dialog.ShowDialog(this) == DialogResult.OK) target.Text = dialog.FileName;
    }

    private void RescanTools()
    {
        _ffmpegStatus.Text = "scanning…";
        _sofficeStatus.Text = "scanning…";
        Update();

        var probe = _settings.Clone();
        probe.FfmpegPath = _ffmpegPath.Text.Trim();
        probe.LibreOfficePath = _sofficePath.Text.Trim();
        Toolchain.Resolve(probe);
        RefreshToolStatus();
    }

    private void RefreshToolStatus()
    {
        _ffmpegStatus.Text = Toolchain.FfmpegReady
            ? $"found: {Toolchain.Ffmpeg}  ·  {Toolchain.FfmpegVersion}"
            : Toolchain.FfmpegError ?? "ffmpeg.exe not found.";
        _ffmpegStatus.ForeColor = Toolchain.FfmpegReady ? Theme.Ok : Theme.Error;

        _sofficeStatus.Text = Toolchain.OfficeReady
            ? $"found: {Toolchain.Soffice}"
            : "not found — document conversions are disabled.";
        _sofficeStatus.ForeColor = Toolchain.OfficeReady ? Theme.Ok : Dark.Muted;
    }

    private void InstallLibreOffice()
    {
        var confirm = MessageBox.Show(this,
            "This opens a terminal and runs:\n\nwinget install --id TheDocumentFoundation.LibreOffice -e\n\nContinue?",
            "Install LibreOffice", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        LaunchWinget("TheDocumentFoundation.LibreOffice");
    }

    private void LaunchWinget(string packageId)
    {
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", $"/k winget install --id {packageId} -e --accept-package-agreements --accept-source-agreements")
            {
                UseShellExecute = true,
            });
            MessageBox.Show(this,
                "When the installer finishes, press Re-scan to pick up the new tool.",
                "Kyklos", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not start winget:\n{ex.Message}", "Kyklos",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleSendTo()
    {
        try
        {
            if (ShellIntegration.SendToInstalled) ShellIntegration.UninstallSendTo();
            else ShellIntegration.InstallSendTo();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not update the Send to shortcut:\n{ex.Message}", "Kyklos",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        _sendToButton.Text = ShellIntegration.SendToInstalled ? "Remove" : "Install";
    }

    private static string LabelFor(string overwriteMode)
        => OverwriteChoices.FirstOrDefault(c => c.Value == overwriteMode).Label ?? OverwriteChoices[0].Label;

    private static string ValueFor(string label)
        => OverwriteChoices.FirstOrDefault(c => c.Label == label).Value ?? OverwriteModes.Rename;

    private void Save()
    {
        _settings.FfmpegPath = NullIfEmpty(_ffmpegPath.Text);
        _settings.LibreOfficePath = NullIfEmpty(_sofficePath.Text);
        _settings.OutputMode = _subfolder.Checked ? OutputModes.Subfolder : OutputModes.Beside;
        _settings.SubfolderName = string.IsNullOrWhiteSpace(_subfolderName.Text) ? "converted" : _subfolderName.Text.Trim();
        _settings.OverwriteMode = ValueFor(_overwrite.SelectedItem?.ToString() ?? OverwriteChoices[0].Label);
        _settings.Quality = _quality.SelectedItem?.ToString() ?? Quality.Balanced;
        _settings.EnableDragSurface = _enableSurface.Checked;
        _settings.DragModifiers = _trigger.SelectedItem?.ToString() ?? "Ctrl+Alt";
        _settings.Hotkey = string.IsNullOrWhiteSpace(_hotkey.Text) ? "Ctrl+Alt+C" : _hotkey.Text.Trim();
        _settings.ShowDropTarget = _showDropTarget.Checked;
        _settings.StartWithWindows = _startup.Checked;

        _settings.Save();
        ShellIntegration.SetStartup(_settings.StartWithWindows);

        DialogResult = DialogResult.OK;
        Close();
    }

    private static string? NullIfEmpty(string value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
