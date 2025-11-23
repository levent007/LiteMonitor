using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using LiteMonitor.src.Core;

namespace LiteMonitor.ThemeEditor
{
    /// <summary>
    /// 实时预览控件（独立于 LiteMonitor 主程序）
    /// - 使用 Mock 数据
    /// - 使用 UILayout + UIRenderer 渲染
    /// - 背景 + 边框 + DPI 处理
    /// </summary>
    public class ThemePreviewControl : Panel
    {
        private Theme? _theme;
        private UILayout? _layout;
        private readonly List<GroupLayoutInfo> _groups = new();

        public ThemePreviewControl()
        {
            DoubleBuffered = true;
            BackColor = Color.White;
            BorderStyle = BorderStyle.FixedSingle;
            Padding = new Padding(5);
        }

        /// <summary>
        /// 编辑器传入主题
        /// </summary>
        public void SetTheme(Theme theme)
        {
            _theme = theme;
            _layout = new UILayout(_theme);

            BuildMockData();
            Invalidate();
        }

        /// <summary>
        /// 构造 Mock 数据用于预览
        /// </summary>
        private void BuildMockData()
        {
            _groups.Clear();

            _groups.Add(new GroupLayoutInfo("CPU", new()
            {
                new MetricItem { Key = "CPU.Load", Value = 23, DisplayValue = 23, Label = "CPU 使用率" },
                new MetricItem { Key = "CPU.Temp", Value = 65, DisplayValue = 65, Label = "CPU 温度" }
            }));

            _groups.Add(new GroupLayoutInfo("GPU", new()
            {
                new MetricItem { Key = "GPU.Load", Value = 90, DisplayValue = 90, Label = "GPU 使用率" },
                new MetricItem { Key = "GPU.Temp", Value = 65, DisplayValue = 65, Label = "GPU 温度" },
                new MetricItem { Key = "GPU.VRAM", Value = 41, DisplayValue = 41, Label = "VRAM 占用" }
            }));

            _groups.Add(new GroupLayoutInfo("MEM", new()
            {
                new MetricItem { Key = "MEM.Load", Value = 68, DisplayValue = 68, Label = "内存占用" }
            }));

            _groups.Add(new GroupLayoutInfo("DISK", new()
            {
                new MetricItem { Key = "DISK.Read", Value = 1024 * 500, Label = "磁盘读取" },
                new MetricItem { Key = "DISK.Write", Value = 1024 * 3320, Label = "磁盘写入" }
            }));

            _groups.Add(new GroupLayoutInfo("NET", new()
            {
                new MetricItem { Key = "NET.Up", Value = 1024 * 80, Label = "上传速度" },
                new MetricItem { Key = "NET.Down", Value = 1024 * 38000, Label = "下载速度" }
            }));

            if (_layout != null)
                _layout.Build(_groups);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);

            if (_theme == null || _layout == null)
                return;

            // 背景
            e.Graphics.Clear(Color.FromArgb(245, 245, 245));

            // 内边距区域
            Rectangle content = new Rectangle(
                Padding.Left,
                Padding.Top,
                Width - Padding.Left - Padding.Right,
                Height - Padding.Top - Padding.Bottom
            );

            // 将渲染区域限制在 content 内
            Region = new Region(content);

            try
            {
                // 渲染所有组
                UIRenderer.Render(e.Graphics, _groups, _theme);
            }
            catch (Exception ex)
            {
                e.Graphics.DrawString(
                    $"Render Error:\n{ex.Message}",
                    this.Font,
                    Brushes.Red,
                    new PointF(5, 5)
                );
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);

            if (_layout != null)
            {
                _layout.Build(_groups);
                Invalidate();
            }
        }
    }
}
