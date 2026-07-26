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
using System.Drawing;
using System.Windows.Forms;

namespace LunchHelper
{
    /// <summary>
    /// 自绘大字号数字触摸键盘（0-9），适用于无物理键盘的纯触控环境。
    /// 自由输入（上限 32 位），点击「确认」提交校验（类似手机 PIN）。
    /// 防暴破锁定时可整体禁用（含「确认」按钮）。
    /// </summary>
    public class NumericPad : UserControl
    {
        private TextBox _display;
        private TableLayoutPanel _grid;
        private Button _confirmBtn;
        private string _buffer = "";
        private bool _locked;
        private const int MaxPin = 32; // 输入上限（对 PIN 近乎无限，仅防极端溢出）

        public event Action<string> CodeSubmitted;

        public NumericPad()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            this.BackColor = Color.Black;

            _display = new TextBox
            {
                ReadOnly = true,
                TextAlign = HorizontalAlignment.Center,
                Font = new Font("Segoe UI", 30F, FontStyle.Bold),
                Height = 54,
                Dock = DockStyle.Top,
                BackColor = Color.Black,
                ForeColor = Color.White,
                BorderStyle = BorderStyle.None,
                Margin = new Padding(0, 0, 0, 12)
            };

            _grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 4,
                Padding = new Padding(4),
                BackColor = Color.Black
            };
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            _grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            for (int i = 0; i < 4; i++)
                _grid.RowStyles.Add(new RowStyle(SizeType.Percent, 25f));

            string[] digits = { "1", "2", "3", "4", "5", "6", "7", "8", "9" };
            int idx = 0;
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                    _grid.Controls.Add(MakeButton(digits[idx++]), c, r);

            _grid.Controls.Add(MakeButton("删除", "DEL"), 0, 3);
            _grid.Controls.Add(MakeButton("0"), 1, 3);
            _grid.Controls.Add(MakeButton("清空", "CLR"), 2, 3);

            _confirmBtn = new Button
            {
                Text = "确认",
                Dock = DockStyle.Bottom,
                Height = 56,
                Font = new Font("Segoe UI", 24F, FontStyle.Bold),
                BackColor = Color.FromArgb(0, 120, 215),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Margin = new Padding(0)
            };
            _confirmBtn.FlatAppearance.BorderSize = 0;
            _confirmBtn.Click += (s, e) => Submit();

            this.Controls.Add(_grid);
            this.Controls.Add(_display);
            this.Controls.Add(_confirmBtn);

            RefreshDisplay();
        }

        private Button MakeButton(string text, string tag = null)
        {
            var btn = new Button
            {
                Text = text,
                Tag = tag ?? text,
                Dock = DockStyle.Fill,
                Font = new Font("Segoe UI", 26F, FontStyle.Bold),
                Margin = new Padding(6),
                BackColor = Color.FromArgb(30, 30, 30),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
            btn.FlatAppearance.BorderColor = Color.FromArgb(70, 70, 70);
            btn.Click += (s, e) => OnKey(btn.Tag as string);
            return btn;
        }

        private void OnKey(string key)
        {
            if (_locked) return;
            if (key == "DEL")
            {
                if (_buffer.Length > 0) _buffer = _buffer.Substring(0, _buffer.Length - 1);
            }
            else if (key == "CLR")
            {
                _buffer = "";
            }
            else if (key.Length == 1 && char.IsDigit(key[0]))
            {
                if (_buffer.Length < MaxPin) _buffer += key;
            }
            RefreshDisplay();
        }

        private void Submit()
        {
            if (_locked || _buffer.Length == 0) return;
            CodeSubmitted?.Invoke(_buffer);
        }

        private void RefreshDisplay()
        {
            _display.Text = new string('●', _buffer.Length);
        }

        public void Clear()
        {
            _buffer = "";
            RefreshDisplay();
        }

        /// <summary>防暴破：锁定时禁用全部按键（含「确认」）。</summary>
        public void SetLocked(bool locked)
        {
            _locked = locked;
            foreach (Control c in _grid.Controls) c.Enabled = !locked;
            if (_confirmBtn != null) _confirmBtn.Enabled = !locked;
        }

        public bool Locked => _locked;
    }
}
