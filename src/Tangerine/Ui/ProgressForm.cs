using Tangerine.Core;

namespace Tangerine.Ui;

/// <summary>Non-modal progress window for a batch conversion, with cancellation.</summary>
internal sealed class ProgressForm : Form
{
    private readonly TextBox _log;
    private readonly ProgressBar _bar;
    private readonly Label _headline;
    private readonly Button _close;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly int _total;
    private bool _running = true;

    public ProgressForm(string title, int total)
    {
        _total = total;

        Text = "Tangerine — converting";
        Dark.Apply(this);
        Size = new Size(580, 400);
        MinimumSize = new Size(460, 320);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;

        _headline = Dark.Label($"{title}", bold: true);
        _headline.Dock = DockStyle.Top;
        _headline.Padding = new Padding(16, 14, 16, 6);

        _bar = new ProgressBar
        {
            Dock = DockStyle.Top,
            Height = 8,
            Style = ProgressBarStyle.Continuous,
            Maximum = Math.Max(1, total),
            MarqueeAnimationSpeed = 0,
        };

        _log = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = Dark.FieldBack,
            ForeColor = Dark.Text,
            BorderStyle = BorderStyle.FixedSingle,
            Font = Theme.Font(11.5f),
        };

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Dark.Back };
        _close = Dark.Secondary("Cancel");
        _close.Click += (_, _) => OnCancelClicked();
        _close.Anchor = AnchorStyles.Right | AnchorStyles.Top;
        _close.Location = new Point(bottom.Width - _close.Width - 16, 10);
        bottom.Resize += (_, _) => _close.Location = new Point(bottom.Width - _close.Width - 16, 10);
        bottom.Controls.Add(_close);

        var spacer = new Panel { Dock = DockStyle.Top, Height = 8, BackColor = Dark.Back };

        Controls.Add(_log);
        Controls.Add(spacer);
        Controls.Add(_bar);
        Controls.Add(_headline);
        Controls.Add(bottom);

        FormClosing += (_, e) =>
        {
            if (!_running) return;
            e.Cancel = true;
            OnCancelClicked();
        };
    }

    public CancellationToken Token => _cancellation.Token;

    public void Report(BatchProgress progress)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Report(progress));
            return;
        }

        if (progress.Result is null)
        {
            _headline.Text = $"Converting {progress.Done + 1} of {progress.Total}: {Path.GetFileName(progress.Source)}";
            return;
        }

        _bar.Value = Math.Min(_bar.Maximum, progress.Done);
        AppendLine(Describe(progress.Result));
    }

    public void MarkCancelled()
    {
        if (InvokeRequired)
        {
            BeginInvoke(MarkCancelled);
            return;
        }
        AppendLine("cancelled by user.");
    }

    public void Finish(IReadOnlyList<ConvertResult> results)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Finish(results));
            return;
        }

        _running = false;
        _bar.Value = _bar.Maximum;

        var ok = results.Count(r => r.Success && !r.Skipped);
        var skipped = results.Count(r => r.Skipped);
        var failed = results.Count(r => !r.Success);
        var totalBytes = results.Where(r => r.Success).Sum(r => r.Bytes);

        var summary = $"Done — {ok} converted";
        if (skipped > 0) summary += $", {skipped} skipped";
        if (failed > 0) summary += $", {failed} failed";
        if (totalBytes > 0) summary += $"  ·  {Paths.HumanSize(totalBytes)} written";

        _headline.Text = summary;
        AppendLine(string.Empty);
        AppendLine(summary);

        _close.Text = "Close";
    }

    private void OnCancelClicked()
    {
        if (!_running)
        {
            Close();
            return;
        }

        _close.Enabled = false;
        _close.Text = "Cancelling…";
        _cancellation.Cancel();
    }

    private static string Describe(ConvertResult result)
    {
        if (result.Skipped) return $"skipped   {result.SourceName}";
        if (!result.Success) return $"failed    {result.SourceName}  —  {result.Error}";
        return $"ok        {result.SourceName}  →  {Path.GetFileName(result.Output)}  ({Paths.HumanSize(result.Bytes)})";
    }

    private void AppendLine(string line)
    {
        _log.AppendText(line + Environment.NewLine);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _cancellation.Dispose();
        base.Dispose(disposing);
    }
}
