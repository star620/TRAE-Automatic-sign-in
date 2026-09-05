using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace TraeCheckin;

/// <summary>
/// 积分趋势折线图：根据签到历史绘制最近若干天的积分变化。
/// </summary>
public class HistoryChart : Control
{
    private readonly List<(DateTime Date, double Credits)> _points = new();
    private int _maxDays = 14;

    public HistoryChart()
    {
        DoubleBuffered = true;
        BackColor = Color.White;
    }

    /// <summary>按日期去重（保留每天最后一条）后设置数据，并只保留最近 maxDays 天。</summary>
    public void SetData(IEnumerable<(DateTime Date, double Credits)> data, int maxDays = 14)
    {
        _maxDays = maxDays;
        _points.Clear();
        var byDate = new Dictionary<DateTime, double>();
        foreach (var (date, credits) in data)
        {
            var d = date.Date;
            byDate[d] = credits;
        }
        foreach (var d in byDate.Keys.OrderBy(x => x))
            _points.Add((d, byDate[d]));
        if (_points.Count > _maxDays)
            _points.RemoveRange(0, _points.Count - _maxDays);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_points.Count == 0)
        {
            using var f = new Font("Segoe UI", 9);
            using var b = new SolidBrush(Color.FromArgb(148, 163, 184));
            g.DrawString("暂无签到数据", f, b, new PointF(12 * MainForm.DpiScale, 12 * MainForm.DpiScale));
            return;
        }

        // 边距按显示缩放换算：高缩放下字体像素变大，固定 40/26px 边距会裁剪 Y 轴刻度与日期
        float k = MainForm.DpiScale;
        float padLeft = 40 * k, padRight = 16 * k, padTop = 16 * k, padBottom = 26 * k;
        float w = Width - padLeft - padRight;
        float h = Height - padTop - padBottom;

        double minV = _points.Min(p => p.Credits);
        double maxV = _points.Max(p => p.Credits);
        if (maxV - minV < 1) { maxV += 1; minV = Math.Max(0, minV - 1); }
        double range = maxV - minV;

        var pts = new PointF[_points.Count];
        for (int i = 0; i < _points.Count; i++)
        {
            float x = padLeft + w * i / Math.Max(1, _points.Count - 1);
            float y = padTop + (float)(h * (1 - (_points[i].Credits - minV) / range));
            pts[i] = new PointF(x, y);
        }

        using (var gridPen = new Pen(Color.FromArgb(226, 232, 240)))
            for (int i = 0; i <= 4; i++)
            {
                float y = padTop + h * i / 4;
                g.DrawLine(gridPen, padLeft, y, padLeft + w, y);
            }

        using (var linePen = new Pen(Color.FromArgb(59, 130, 246), 2))
        {
            if (pts.Length == 1)
                g.FillEllipse(new SolidBrush(Color.FromArgb(59, 130, 246)), pts[0].X - 4, pts[0].Y - 4, 8, 8);
            else
                g.DrawLines(linePen, pts);
        }

        using (var dotBrush = new SolidBrush(Color.FromArgb(59, 130, 246)))
            foreach (var p in pts)
                g.FillEllipse(dotBrush, p.X - 3, p.Y - 3, 6, 6);

        using (var font = new Font("Segoe UI", 8))
        using (var textBrush = new SolidBrush(Color.FromArgb(100, 116, 139)))
        {
            var first = _points[0].Date.ToString("MM-dd");
            var last = _points[^1].Date.ToString("MM-dd");
            g.DrawString(first, font, textBrush, padLeft, Height - padBottom + 5 * k);
            var sz = g.MeasureString(last, font);
            g.DrawString(last, font, textBrush, padLeft + w - sz.Width, Height - padBottom + 5 * k);
            g.DrawString(maxV.ToString("0"), font, textBrush, 4 * k, padTop - 5 * k);
            g.DrawString(minV.ToString("0"), font, textBrush, 4 * k, padTop + h - 5 * k);
        }
    }
}
