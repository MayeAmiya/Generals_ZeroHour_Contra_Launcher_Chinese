using System;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace Contra
{
    /// <summary>
    ///     Bootstrap / repair progress window. While it is open the launcher's main form is
    ///     disabled - the user watches the progress (current file, per-file bar, speed, ETA,
    ///     overall count) and may cancel the pass; nothing else is interactive. All texts
    ///     follow the launcher's current language (no bilingual lines). All updates happen
    ///     on the UI thread (the repair pass runs inside Form1_Shown's async context).
    /// </summary>
    internal class FileRepairProgressForm : Form
    {
        private readonly Label titleLabel;
        private readonly Label fileLabel;
        private readonly ProgressBar fileBar;
        private readonly Label statLabel;
        private readonly Label overallLabel;
        private readonly Button cancelButton;
        private readonly bool chinese;
        private int lastPercent = -1;
        private int totalCount;
        private int doneCount;
        private string overallStats;

        /// <summary>Cancels the running repair pass when the user hits the button.</summary>
        public CancellationTokenSource Cancellation { get; } = new CancellationTokenSource();

        /// <summary>True once ShowCompleteThenClose started; the outer finally must not dispose.</summary>
        public bool AutoClosing { get; private set; }

        public FileRepairProgressForm(int totalFiles)
        {
            chinese = Globals.currentLanguage == "CN";

            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            ControlBox = false; // no X: only the cancel button ends the pass early
            DoubleBuffered = true;
            Text = "Contra Launcher";
            BackColor = Color.FromArgb(30, 30, 46);
            Size = new Size(500, 200);

            titleLabel = new Label
            {
                AutoSize = false,
                Location = new Point(14, 12),
                Size = new Size(460, 24),
                ForeColor = Color.White,
                Font = new Font("Calibri", 15F, GraphicsUnit.Pixel),
                Text = chinese ? "正在检查文件" : "Checking files",
            };
            fileLabel = new Label
            {
                AutoSize = false,
                Location = new Point(14, 44),
                Size = new Size(460, 20),
                ForeColor = Color.FromArgb(200, 200, 210),
                Font = new Font("Calibri", 12F, GraphicsUnit.Pixel),
                Text = "",
            };
            fileBar = new ProgressBar
            {
                Location = new Point(14, 68),
                Size = new Size(460, 18),
            };
            statLabel = new Label
            {
                AutoSize = false,
                Location = new Point(14, 92),
                Size = new Size(460, 20),
                ForeColor = Color.FromArgb(160, 220, 160),
                Font = new Font("Calibri", 12F, GraphicsUnit.Pixel),
                Text = "",
            };
            overallLabel = new Label
            {
                AutoSize = false,
                Location = new Point(14, 122),
                Size = new Size(370, 20),
                ForeColor = Color.White,
                Font = new Font("Calibri", 13F, GraphicsUnit.Pixel),
                Text = "",
            };
            cancelButton = new Button
            {
                Location = new Point(392, 118),
                Size = new Size(82, 28),
                Text = chinese ? "取消" : "Cancel",
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(60, 60, 80),
                ForeColor = Color.White,
            };
            cancelButton.Click += (sender, args) =>
            {
                Cancellation.Cancel();
                cancelButton.Enabled = false;
                titleLabel.Text = chinese ? "正在取消..." : "Cancelling...";
            };

            Controls.Add(titleLabel);
            Controls.Add(fileLabel);
            Controls.Add(fileBar);
            Controls.Add(statLabel);
            Controls.Add(overallLabel);
            Controls.Add(cancelButton);

            totalCount = totalFiles;
            RenderOverall();
        }

        /// <summary>Phase caption in the launcher's language.</summary>
        public void SetPhase(string text)
        {
            titleLabel.Text = text;
        }

        /// <summary>
        ///     Current file name and per-file percent. The progress bar only touches the
        ///     control tree when the rounded percent actually changes.
        /// </summary>
        public void SetFile(string name, int percent)
        {
            fileLabel.Text = name;
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;
            if (percent != lastPercent)
            {
                fileBar.Value = percent;
                lastPercent = percent;
            }
        }

        /// <summary>Downloaded bytes, total bytes, speed and ETA for the current file.</summary>
        public void SetStats(long received, long total, double bytesPerSecond, string eta)
        {
            if (total > 0)
                statLabel.Text = FormatSize(received) + " / " + FormatSize(total)
                    + "   ·   " + FormatSize((long)bytesPerSecond) + "/s"
                    + "   ·   " + (chinese ? "剩余 " : "ETA ") + eta;
            else
                statLabel.Text = "";
        }

        /// <summary>Clears the speed/ETA row (e.g. while checking a file).</summary>
        public void ClearStats()
        {
            statLabel.Text = "";
        }

        public void SetOverall(int done)
        {
            doneCount = done;
            RenderOverall();
        }

        /// <summary>
        ///     Overall speed / ETA line for the whole sync, rendered next to the count.
        /// </summary>
        public void SetOverallStats(long received, long total, double bytesPerSecond, string eta)
        {
            if (total > 0)
                overallStats = FormatSize(received) + " / " + FormatSize(total)
                    + "   ·   " + FormatSize((long)bytesPerSecond) + "/s"
                    + "   ·   " + (chinese ? "剩余 " : "ETA ") + eta;
            else
                overallStats = "";
            RenderOverall();
        }

        public void ClearOverallStats()
        {
            overallStats = "";
            RenderOverall();
        }

        public void SetTotal(int total)
        {
            totalCount = total;
            RenderOverall();
        }

        private void RenderOverall()
        {
            overallLabel.Text = (chinese ? "总进度: " : "Overall: ") + doneCount + " / " + totalCount
                + (string.IsNullOrEmpty(overallStats) ? "" : "   ·   " + overallStats);
        }

        /// <summary>
        ///     Shows the completion state and closes the window by itself after a short
        ///     pause - nothing for the user to click.
        /// </summary>
        public void ShowCompleteThenClose(int milliseconds)
        {
            AutoClosing = true;
            SetPhase(chinese ? "下载完成" : "Everything restored");
            SetFile("", 100);
            ClearStats();
            ClearOverallStats();
            cancelButton.Visible = false;

            System.Windows.Forms.Timer closeTimer = new System.Windows.Forms.Timer { Interval = milliseconds };
            closeTimer.Tick += (sender, args) =>
            {
                ((System.Windows.Forms.Timer)sender).Stop();
                ((System.Windows.Forms.Timer)sender).Dispose();
                Close();
            };
            closeTimer.Start();
        }

        private static string FormatSize(long bytes)
        {
            if (bytes >= 1073741824) return (bytes / 1073741824.0).ToString("F2") + " GB";
            if (bytes >= 1048576) return (bytes / 1048576.0).ToString("F1") + " MB";
            if (bytes >= 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            return bytes + " B";
        }
    }
}
