using System;
using System.Drawing;
using System.Windows.Forms;

namespace Contra
{
    /// <summary>
    ///     Bootstrap / repair progress window: what is currently happening, the current
    ///     file with its progress bar, download speed, remaining time and the overall file
    ///     count. Top-most so it stays visible during the first install. All updates happen
    ///     on the UI thread (the repair pass runs inside Form1_Shown's async context).
    /// </summary>
    internal class FileRepairProgressForm : Form
    {
        private readonly Label titleLabel;
        private readonly Label fileLabel;
        private readonly ProgressBar fileBar;
        private readonly Label statLabel;
        private readonly Label overallLabel;
        private int lastPercent = -1;

        /// <summary>True once ShowCompleteThenClose started; the outer finally must not dispose.</summary>
        public bool AutoClosing { get; private set; }

        public FileRepairProgressForm(int totalFiles)
        {
            FormBorderStyle = FormBorderStyle.FixedToolWindow;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            TopMost = true;
            MinimizeBox = false;
            MaximizeBox = false;
            DoubleBuffered = true;
            Text = "Contra Launcher";
            BackColor = Color.FromArgb(30, 30, 46);
            Size = new Size(500, 196);

            titleLabel = new Label
            {
                AutoSize = false,
                Location = new Point(14, 12),
                Size = new Size(460, 24),
                ForeColor = Color.White,
                Font = new Font("Calibri", 15F, GraphicsUnit.Pixel),
                Text = "正在检查文件 / checking files",
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
                Size = new Size(460, 20),
                ForeColor = Color.White,
                Font = new Font("Calibri", 13F, GraphicsUnit.Pixel),
                Text = "",
            };

            Controls.Add(titleLabel);
            Controls.Add(fileLabel);
            Controls.Add(fileBar);
            Controls.Add(statLabel);
            Controls.Add(overallLabel);

            SetOverall(0, totalFiles);
        }

        /// <summary>Phase caption, e.g. "正在下载 / downloading".</summary>
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
                    + "   ·   剩余 / ETA " + eta;
            else
                statLabel.Text = "";
        }

        /// <summary>Clears the speed/ETA row (e.g. while checking a file).</summary>
        public void ClearStats()
        {
            statLabel.Text = "";
        }

        public void SetOverall(int done, int total)
        {
            overallLabel.Text = "总进度 / overall: " + done + " / " + total;
        }

        /// <summary>
        ///     Shows the completion state and closes the window by itself after a short
        ///     pause - nothing for the user to click.
        /// </summary>
        public void ShowCompleteThenClose(int milliseconds)
        {
            AutoClosing = true;
            SetPhase("下载完成 / everything restored");
            SetFile("", 100);
            ClearStats();

            Timer closeTimer = new Timer { Interval = milliseconds };
            closeTimer.Tick += (sender, args) =>
            {
                ((Timer)sender).Stop();
                ((Timer)sender).Dispose();
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
