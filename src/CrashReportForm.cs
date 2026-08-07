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
    /// 布局采用 TableLayoutPanel 声明式网格（与 WPF/Avalonia 思路一致），不在 InitializeComponent 中
    /// 手工计算坐标，彻底规避不同 DPI/字体下按钮文字被截断、标题与描述重叠等 WinForms 老问题。
    /// </summary>
    internal partial class CrashReportForm : Form
    {
        private readonly Exception _exception;
        private readonly bool _critical;
        // 关键异常时“忽略错误”的位置会被“调试”按钮替换
        private Button _ignoreBtn;
        private Button _debugBtn;

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
            Font = new Font("Segoe UI", 10F);
            Padding = new Padding(0);

            // 主网格：单列，行依次为 标题 / 图标+描述 / 详情(占满) / 关键提示 / 按钮
            var tlp = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 5,
                Padding = new Padding(24, 20, 24, 16),
                BackColor = Color.FromArgb(0x2D, 0x2D, 0x30)
            };
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 标题
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 图标 + 描述
            tlp.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));   // 详情（吸收剩余空间）
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 关键提示条
            tlp.RowStyles.Add(new RowStyle(SizeType.AutoSize));        // 按钮行
            Controls.Add(tlp);

            // 标题
            var title = new Label
            {
                Text = "崩溃啦！（T_T）",
                Font = new Font("Segoe UI", 20F, FontStyle.Bold),
                ForeColor = Color.White,
                Dock = DockStyle.Top,
                AutoSize = true
            };
            tlp.Controls.Add(title, 0, 0);

            // 图标 + 描述：嵌套 2 列表格（图标固定宽，描述占满）
            var head = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 2,
                RowCount = 1,
                Padding = new Padding(0, 12, 0, 12),
                BackColor = Color.Transparent
            };
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56F));
            head.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            head.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var iconPanel = new Panel
            {
                Size = new Size(48, 48),
                Dock = DockStyle.Left,
                BackColor = Color.Transparent
            };
            iconPanel.Paint += (s, e) => DrawExclamation(e.Graphics);
            head.Controls.Add(iconPanel, 0, 0);

            var desc = new Label
            {
                Text = "LunchHelper 碰到了严重错误而无法继续运行。您可以保存下方的错误信息并向他人寻求帮助。如果您认为这是由软件本身的错误所致，请点击下方【反馈问题】按钮。",
                Font = new Font("Segoe UI", 10F),
                ForeColor = Color.FromArgb(0xE0, 0xE0, 0xE0),
                Dock = DockStyle.Fill,
                AutoSize = true
            };
            head.Controls.Add(desc, 1, 0);
            tlp.Controls.Add(head, 0, 1);

            // 异常详情文本框：锚定填满单元格，随窗体缩放
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
                Dock = DockStyle.Fill
            };
            tlp.Controls.Add(detailBox, 0, 2);

            // 关键异常提示条（默认隐藏；AutoSize 行在不可见时不占空间）
            var criticalPanel = new Panel
            {
                Dock = DockStyle.Top,
                Visible = false,
                BackColor = Color.FromArgb(0xC5, 0x1E, 0x1E),
                Padding = new Padding(12, 8, 12, 8)
            };
            var criticalLabel = new Label
            {
                Dock = DockStyle.Fill,
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 9.5F),
                Text = "这个异常是无法被忽略的关键异常，您只能重启或退出应用。很抱歉对您的使用造成不便。",
                TextAlign = ContentAlignment.MiddleLeft,
                AutoSize = true
            };
            criticalPanel.Controls.Add(criticalLabel);
            tlp.Controls.Add(criticalPanel, 0, 3);

            // 按钮：5 列等分的表格，按钮左右锚定填满单元格，文字居中，彻底消除截断
            var btnTable = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                ColumnCount = 5,
                RowCount = 1,
                Padding = new Padding(0, 12, 0, 0),
                BackColor = Color.Transparent
            };
            for (int i = 0; i < 5; i++)
                btnTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20F));
            btnTable.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var copyBtn = CreateFlatButton("复制");
            var feedbackBtn = CreateFlatButton("反馈问题");
            _ignoreBtn = CreateFlatButton("忽略错误");
            _debugBtn = CreateFlatButton("调试");
            var exitBtn = CreateFlatButton("退出应用");
            var restartBtn = CreateFlatButton("重启应用", Color.FromArgb(0x00, 0x78, 0xD4));

            copyBtn.Click += (s, e) =>
            {
                try { Clipboard.SetText(detailBox.Text); copyBtn.Text = "已复制"; }
                catch { copyBtn.Text = "复制失败"; }
            };
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
            _ignoreBtn.DialogResult = DialogResult.Ignore;
            _debugBtn.Visible = false;
            _debugBtn.Click += (s, e) =>
            {
                try { Debugger.Launch(); }
                catch { }
                DialogResult = DialogResult.No;
                Close();
            };
            exitBtn.DialogResult = DialogResult.Abort;
            restartBtn.DialogResult = DialogResult.Retry;

            btnTable.Controls.Add(copyBtn, 0, 0);
            btnTable.Controls.Add(feedbackBtn, 1, 0);
            // 忽略错误 与 调试 共用第 3 列：普通异常显示“忽略错误”，
            // 关键异常时 _ignoreBtn 隐藏、_debugBtn 显示（二者仅一个可见，互不干扰）。
            btnTable.Controls.Add(_ignoreBtn, 2, 0);
            btnTable.Controls.Add(_debugBtn, 2, 0);
            btnTable.Controls.Add(exitBtn, 3, 0);
            btnTable.Controls.Add(restartBtn, 4, 0);
            tlp.Controls.Add(btnTable, 0, 4);

            if (_critical)
            {
                _ignoreBtn.Visible = false;
                _debugBtn.Visible = true;
                criticalPanel.Visible = true;
                AcceptButton = restartBtn;
            }
            else
            {
                AcceptButton = _ignoreBtn;
            }
            CancelButton = exitBtn;
        }

        private static Button CreateFlatButton(string text, Color? backColor = null)
        {
            return new Button
            {
                Text = text,
                FlatStyle = FlatStyle.Flat,
                BackColor = backColor ?? Color.FromArgb(0x3C, 0x3C, 0x41),
                ForeColor = Color.White,
                Height = 36,
                // 左右锚定：宽度跟随单元格自动拉伸，高度固定 36；文字居中，零截断
                Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top,
                Font = new Font("Segoe UI", 9.5F),
                TextAlign = ContentAlignment.MiddleCenter,
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
