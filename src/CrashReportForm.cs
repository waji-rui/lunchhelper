// LunchHelper — 触控锁屏助手
// Copyright (C) 2026  LunchHelper Contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Text;
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 崩溃报告弹窗：仿 ClassIsland 风格，集中展示未捕获异常信息并提供复制/反馈/忽略/退出/重启操作。
    /// 仅在发生未处理异常时弹出，平时不实例化。
    /// </summary>
    internal partial class CrashReportForm : Form
    {
        private readonly Exception _exception;
        private readonly bool _critical;

        public CrashReportForm(Exception ex, bool critical)
        {
            _exception = ex;
            _critical = critical;
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            // 基础窗体：深色内容、原生标题栏（崩溃弹窗本身必须最稳健，不自绘标题栏以免自身失败）
            Text = "LunchHelper 已崩溃";
            Size = new Size(820, 620);
            MinimumSize = new Size(640, 480);
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimizeBox = false;
            BackColor = Color.FromArgb(0x2D, 0x2D, 0x30);
            ForeColor = Color.White;
            Padding = new Padding(0);

            const int margin = 24;
            int clientW = ClientSize.Width;
            int contentW = clientW - margin * 2;

            // 标题
            var title = new Label
            {
                Text = "崩溃啦！（T_T）",
                Font = new Font("Segoe UI", 20F, FontStyle.Bold),
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(margin, 20)
            };
            Controls.Add(title);

            // 红色感叹号图标（自绘，避免依赖外部资源文件）
            const int iconSize = 48;
            var iconPanel = new Panel
            {
                Size = new Size(iconSize, iconSize),
                Location = new Point(margin, 76),
                BackColor = Color.Transparent
            };
            iconPanel.Paint += (s, e) => DrawExclamation(e.Graphics);
            Controls.Add(iconPanel);

            // 说明文本：自动换行、自动高度，避免截断或重叠
            int descX = margin + iconSize + 12;
            int descW = clientW - descX - margin;
            var desc = new Label
            {
                Text = "LunchHelper 碰到了严重错误而无法继续运行。您可以保存下方的错误信息并向他人寻求帮助。如果您认为这是由软件本身的错误所致，请点击下方【反馈问题】按钮。",
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0),
                Location = new Point(descX, 76),
                Size = new Size(descW, 64),
                MaximumSize = new Size(descW, 0),
                AutoSize = true,
                TextAlign = ContentAlignment.TopLeft
            };
            Controls.Add(desc);

            // 关键异常提示条（默认折叠；Dock.Bottom 会自然与按钮面板上下堆叠）
            var criticalPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 0,
                BackColor = Color.FromArgb(0xC5, 0x1E, 0x1E),
                Padding = new Padding(16, 10, 16, 10)
            };
            var criticalLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F),
                Text = "这个异常是无法被忽略的关键异常，您只能重启或退出应用。很抱歉对您的使用造成不便。",
                TextAlign = ContentAlignment.MiddleLeft
            };
            criticalPanel.Controls.Add(criticalLabel);
            Controls.Add(criticalPanel);

            // 底部按钮面板
            var btnPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = 72,
                BackColor = Color.FromArgb(0x2D, 0x2D, 0x30),
                Padding = new Padding(16, 14, 16, 14)
            };
            Controls.Add(btnPanel);

            // 异常详情文本框：锚定四周，随窗体缩放
            int detailTop = Math.Max(160, desc.Bottom + 16);
            var detailBox = new TextBox
            {
                Name = "detailBox",
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                BackColor = Color.FromArgb(0x1E, 0x1E, 0x1E),
                ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0),
                Font = new Font("Consolas", 9.5F),
                Text = BuildDetail(),
                Location = new Point(margin, detailTop),
                Size = new Size(contentW, ClientSize.Height - detailTop - btnPanel.Height - criticalPanel.Height - 8),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };
            Controls.Add(detailBox);

            // 按钮：加宽、加高、加大间距，避免文字截断
            const int btnW = 120;
            const int btnH = 36;
            const int btnGap = 12;
            int btnY = btnPanel.Height / 2 - btnH / 2;
            int totalBtnW = btnW * 5 + btnGap * 4;
            // 按钮面板已 Dock.Bottom 占满窗体宽度，故用窗体客户区宽度计算居中；
            // 避免在 InitializeComponent 早期读取 btnPanel.ClientSize.Width 可能尚未完成布局的风险。
            int btnX = (clientW - totalBtnW) / 2;

            // 复制
            var copyBtn = CreateFlatButton("复制", Color.FromArgb(0x3C, 0x3C, 0x41), btnW, btnH);
            copyBtn.Location = new Point(btnX, btnY);
            copyBtn.Click += (s, e) =>
            {
                try
                {
                    Clipboard.SetText(detailBox.Text);
                    copyBtn.Text = "已复制";
                }
                catch
                {
                    copyBtn.Text = "复制失败";
                }
            };
            btnPanel.Controls.Add(copyBtn);
            btnX += btnW + btnGap;

            // 反馈问题
            var feedbackBtn = CreateFlatButton("反馈问题", Color.FromArgb(0x3C, 0x3C, 0x41), btnW, btnH);
            feedbackBtn.Location = new Point(btnX, btnY);
            feedbackBtn.Click += (s, e) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = "https://github.com/creaconception/lunchhelper/issues",
                        UseShellExecute = true
                    });
                }
                catch { }
            };
            btnPanel.Controls.Add(feedbackBtn);
            btnX += btnW + btnGap;

            // 忽略错误（非关键异常可用）
            var ignoreBtn = CreateFlatButton("忽略错误", Color.FromArgb(0x3C, 0x3C, 0x41), btnW, btnH);
            ignoreBtn.Location = new Point(btnX, btnY);
            ignoreBtn.DialogResult = DialogResult.Ignore;
            btnPanel.Controls.Add(ignoreBtn);

            // 调试（关键异常时替换“忽略错误”）
            var debugBtn = CreateFlatButton("调试", Color.FromArgb(0x3C, 0x3C, 0x41), btnW, btnH);
            debugBtn.Location = new Point(btnX, btnY);
            debugBtn.Visible = false;
            debugBtn.Click += (s, e) =>
            {
                try { Debugger.Launch(); }
                catch { }
                DialogResult = DialogResult.No;
                Close();
            };
            btnPanel.Controls.Add(debugBtn);
            btnX += btnW + btnGap;

            // 退出应用
            var exitBtn = CreateFlatButton("退出应用", Color.FromArgb(0x3C, 0x3C, 0x41), btnW, btnH);
            exitBtn.Location = new Point(btnX, btnY);
            exitBtn.DialogResult = DialogResult.Abort;
            btnPanel.Controls.Add(exitBtn);
            btnX += btnW + btnGap;

            // 重启应用
            var restartBtn = CreateFlatButton("重启应用", Color.FromArgb(0x00, 0x78, 0xD4), btnW, btnH);
            restartBtn.Location = new Point(btnX, btnY);
            restartBtn.DialogResult = DialogResult.Retry;
            btnPanel.Controls.Add(restartBtn);

            if (_critical)
            {
                ignoreBtn.Visible = false;
                debugBtn.Visible = true;
                criticalPanel.Height = 44;
                AcceptButton = restartBtn;
            }
            else
            {
                AcceptButton = ignoreBtn;
            }
            CancelButton = exitBtn;
        }

        private static Button CreateFlatButton(string text, Color backColor, int width, int height)
        {
            return new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor,
                ForeColor = Color.White,
                Size = new Size(width, height),
                Font = new Font("Segoe UI", 9.5F),
                FlatAppearance = { BorderSize = 0 }
            };
        }

        private string BuildDetail()
        {
            var sb = new StringBuilder();
            if (_exception != null)
            {
                sb.AppendLine(_exception.GetType().FullName + ": " + _exception.Message);
                if (!string.IsNullOrEmpty(_exception.StackTrace))
                    sb.AppendLine(_exception.StackTrace);
                Exception inner = _exception.InnerException;
                while (inner != null)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Inner Exception ---");
                    sb.AppendLine(inner.GetType().FullName + ": " + inner.Message);
                    if (!string.IsNullOrEmpty(inner.StackTrace))
                        sb.AppendLine(inner.StackTrace);
                    inner = inner.InnerException;
                }
            }
            else
            {
                sb.AppendLine("未知异常");
            }
            sb.AppendLine();
            sb.AppendLine("OS: " + Environment.OSVersion);
            sb.AppendLine("Version: " + ConfigManager.Version);
            sb.AppendLine("Date: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            return sb.ToString();
        }

        private void DrawExclamation(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(Color.FromArgb(0xF0, 0x3C, 0x3C)))
                g.FillEllipse(brush, 0, 0, 44, 44);
            using (var pen = new Pen(Color.White, 3))
            {
                g.DrawLine(pen, 22, 10, 22, 28);
                g.FillEllipse(Brushes.White, 19, 32, 6, 6);
            }
        }
    }
}
