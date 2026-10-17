using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;

namespace SlotsInstaller
{
    // 2.5.9 About-card icons, drawn like the other glyphs (about 16 px at 100 %, scaled by dpi):
    // GitHub (the Octicons mark-github path, MIT), Patreon (circle and bar), Nexus Mods (its emblem) and ColorSync (its favicon).
    internal static class BrandGlyphs
    {
        // Octicons "mark-github" 16x16 (https://github.com/primer/octicons, MIT licence).
        private const string GitHubPath = "M8 0C3.58 0 0 3.58 0 8c0 3.54 2.29 6.53 5.47 7.59.4.07.55-.17.55-.38 0-.19-.01-.82-.01-1.49-2.01.37-2.53-.49-2.69-.94-.09-.23-.48-.94-.82-1.13-.28-.15-.68-.52-.01-.53.63-.01 1.08.58 1.23.82.72 1.21 1.87.87 2.33.66.07-.52.28-.87.51-1.07-1.78-.2-3.64-.89-3.64-3.95 0-.87.31-1.59.82-2.15-.08-.2-.36-1.02.08-2.12 0 0 .67-.21 2.2.82.64-.18 1.32-.27 2-.27.68 0 1.36.09 2 .27 1.53-1.04 2.2-.82 2.2-.82.44 1.1.16 1.92.08 2.12.51.56.82 1.27.82 2.15 0 3.07-1.87 3.75-3.65 3.95.29.25.54.73.54 1.48 0 1.07-.01 1.93-.01 2.2 0 .21.15.46.55.38A8.013 8.013 0 0016 8c0-4.42-3.58-8-8-8z";

        // Nexus Mods emblem (next.nexusmods.com/assets/images/default/logo-emblem.svg, viewBox 0 0 162 162), without its
        // black outline path (invisible on the dark tile): four orange arrow tails, the orange disc, four white arrows.
        private const string NexusOrangeA = "M56.3,88.4l0.7,28.3l-7-5.7c-7.8,12.7-10.3,25-6.6,34.1l1.3,3.2l-3.2-1.4c-7.3-3.2-13.9-7.7-19.4-13.5 l-0.3-0.3l-0.1-0.5c-0.4-3.5-0.2-7.3,0.7-11.2l0-0.1c1.3-4.9,3.2-9.8,5.6-14.7c1.5-3.1,3.3-6.2,5.3-9.2l-6.1-5L56.3,88.4z";
        private const string NexusOrangeB = "M105.9,74.1l-0.7-28.3l7,5.7c7.8-12.7,10.3-25,6.6-34.1l-1.3-3.2l3.2,1.4c7.3,3.2,13.9,7.7,19.4,13.5 l0.3,0.3l0.1,0.5c0.4,3.5,0.2,7.3-0.7,11.2l0,0.1c-1.3,4.9-3.2,9.8-5.6,14.7c-1.5,3.1-3.3,6.2-5.3,9.2l6.1,5L105.9,74.1z";
        private const string NexusOrangeC = "M88.5,105.4l28.3-0.7l-5.7,7c12.7,7.8,25,10.3,34.1,6.6l3.2-1.3l-1.4,3.2c-3.2,7.3-7.7,13.9-13.5,19.4 l-0.3,0.3l-0.5,0.1c-3.5,0.4-7.3,0.2-11.2-0.7l-0.1,0c-4.9-1.3-9.8-3.2-14.7-5.6c-3.1-1.5-6.2-3.3-9.2-5.3l-5,6.1L88.5,105.4z";
        private const string NexusOrangeD = "M74.1,57.6l-28.3,0.7l5.7-7c-12.7-7.8-25-10.3-34.1-6.6L14.3,46l1.4-3.2c3.2-7.3,7.7-13.9,13.5-19.4 l0.3-0.3L30,23c3.5-0.4,7.3-0.2,11.2,0.7l0.1,0c4.9,1.3,9.8,3.2,14.7,5.6c3.1,1.5,6.2,3.3,9.2,5.3l5-6.1L74.1,57.6z";
        private const string NexusWhiteA = "M59.3,59.5c-3.5-1.6-6.1-3.2-8.7-5.1c-4-2.8-7.7-5.9-10.8-9.2c-7.6-7.7-11.6-15.6-10.5-22.1L27,25.6 c-5.5,5.8-12.8,16-12.9,20.4c0.1,0.5,0.1,0.5,0.1,0.5c1,3.4,2.6,6.8,4.9,10.1l0,0.1c3,4.8,8.9,12.7,29.9,21.9l-3.7,7l28.3-7.6 L63.5,51.5L59.3,59.5z";
        private const string NexusWhiteB = "M103.3,103.5c3.5,1.6,6.1,3.2,8.7,5.1c4,2.8,7.7,5.9,10.8,9.2c7.6,7.7,11.6,15.6,10.5,22.1l2.3-2.4 c5.5-5.8,12.8-16,12.9-20.4c-0.1-0.5-0.1-0.5-0.1-0.5c-1-3.4-2.6-6.8-4.9-10.1l0-0.1c-3-4.8-8.9-12.7-29.9-21.9l3.7-7l-28.3,7.6 l10.2,26.2L103.3,103.5z";
        private const string NexusWhiteC = "M104,59.3c1.6-3.5,3.2-6.1,5.1-8.7c2.8-4,5.9-7.7,9.2-10.8c7.7-7.6,15.6-11.6,22.1-10.5L138,27 c-5.8-5.5-16-12.8-20.4-12.9c-0.5,0.1-0.5,0.1-0.5,0.1c-3.4,1-6.8,2.6-10.1,4.9l-0.1,0c-4.8,3-12.7,8.9-21.9,29.9l-7-3.7 l7.6,28.3L112,63.6L104,59.3z";
        private const string NexusWhiteD = "M58.2,103.2c-1.6,3.5-3.2,6.1-5.1,8.7c-2.8,4-5.9,7.7-9.2,10.8c-7.7,7.6-15.6,11.6-22.1,10.5l2.4,2.3 c5.8,5.5,16,12.8,20.4,12.9c0.5-0.1,0.5-0.1,0.5-0.1c3.4-1,6.8-2.6,10.1-4.9l0.1,0c4.8-3,12.7-8.9,21.9-29.9l7,3.7l-7.6-28.3 L50.3,98.9L58.2,103.2z";

        // Discord logo, 24x24 (as served in the Nexus Mods site footer). Patreon mark, 24x24 (as on colorsync-sf6.pages.dev).
        private const string DiscordPath = "M19.4308 5.26368C18.1561 4.67878 16.7892 4.24785 15.3599 4.00104C15.3339 3.99627 15.3079 4.00818 15.2945 4.03198C15.1187 4.34466 14.9239 4.75258 14.7876 5.0732C13.2503 4.84306 11.721 4.84306 10.2153 5.0732C10.0789 4.74545 9.87707 4.34466 9.70048 4.03198C9.68707 4.00897 9.66107 3.99707 9.63504 4.00104C8.20659 4.24706 6.83963 4.67799 5.56411 5.26368C5.55307 5.26844 5.54361 5.27638 5.53732 5.28669C2.94449 9.16032 2.23421 12.9387 2.58265 16.6703C2.58423 16.6886 2.59447 16.706 2.60867 16.7171C4.31934 17.9734 5.97642 18.7361 7.60273 19.2416C7.62876 19.2496 7.65634 19.24 7.6729 19.2186C8.05761 18.6933 8.40054 18.1393 8.69456 17.5568C8.71192 17.5227 8.69535 17.4822 8.65989 17.4687C8.11594 17.2624 7.598 17.0108 7.09977 16.7251C7.06037 16.7021 7.05721 16.6457 7.09347 16.6187C7.19831 16.5402 7.30318 16.4584 7.4033 16.3759C7.42141 16.3608 7.44665 16.3576 7.46794 16.3671C10.7411 17.8615 14.2846 17.8615 17.5191 16.3671C17.5404 16.3568 17.5657 16.36 17.5846 16.3751C17.6847 16.4576 17.7895 16.5402 17.8952 16.6187C17.9314 16.6457 17.9291 16.7021 17.8897 16.7251C17.3914 17.0163 16.8735 17.2624 16.3288 17.4679C16.2933 17.4814 16.2775 17.5227 16.2949 17.5568C16.5952 18.1385 16.9381 18.6924 17.3157 19.2178C17.3315 19.24 17.3599 19.2496 17.3859 19.2416C19.0201 18.7361 20.6772 17.9734 22.3879 16.7171C22.4028 16.706 22.4123 16.6894 22.4139 16.6711C22.8309 12.357 21.7154 8.60956 19.4568 5.28748C19.4513 5.27638 19.4419 5.26844 19.4308 5.26368ZM9.18335 14.3982C8.19792 14.3982 7.38594 13.4935 7.38594 12.3824C7.38594 11.2713 8.18217 10.3666 9.18335 10.3666C10.1924 10.3666 10.9965 11.2793 10.9807 12.3824C10.9807 13.4935 10.1845 14.3982 9.18335 14.3982ZM15.829 14.3982C14.8435 14.3982 14.0316 13.4935 14.0316 12.3824C14.0316 11.2713 14.8278 10.3666 15.829 10.3666C16.838 10.3666 17.6421 11.2793 17.6264 12.3824C17.6264 13.4935 16.838 14.3982 15.829 14.3982Z";
        private const string PatreonPath = "M0 3h5v18H0V3Zm14.5 0a8.5 8.5 0 1 0 0 17 8.5 8.5 0 0 0 0-17Z";
        internal static readonly Color KofiRed = Color.FromArgb(0xFF, 0x5E, 0x5B);

        internal static bool Handles(ButtonGlyph glyph) { return glyph == ButtonGlyph.GitHub || glyph == ButtonGlyph.Patreon || glyph == ButtonGlyph.Nexus || glyph == ButtonGlyph.ColorSync || glyph == ButtonGlyph.Chat || glyph == ButtonGlyph.Coffee; }

        internal static void Draw(Graphics graphics, ButtonGlyph glyph, Rectangle area, Color color, int dpi)
        {
            float s = dpi / 96F, x = area.Left + area.Width / 2F, y = area.Top + area.Height / 2F;
            SmoothingMode previous = graphics.SmoothingMode; graphics.SmoothingMode = SmoothingMode.AntiAlias;
            try
            {
                using (var brush = new SolidBrush(color))
                using (var pen = new Pen(color, 1.4F * s) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                {
                    if (glyph == ButtonGlyph.GitHub)
                    {
                        // 16 units -> 16 px at 100 %, centred.
                        using (GraphicsPath path = SvgPath.Parse(GitHubPath))
                        using (var m = new Matrix())
                        {
                            m.Translate(x - 8 * s, y - 8 * s); m.Scale(s, s);
                            path.Transform(m); graphics.FillPath(brush, path);
                        }
                    }
                    else if (glyph == ButtonGlyph.Patreon)
                    {
                        // 23 x 18 units (x 0-23, y 3-21) -> 15 px wide at 100 %, centred.
                        float u = 15F * s / 23F;
                        using (var m = new Matrix()) { m.Translate(x - 11.5F * u, y - 12F * u); m.Scale(u, u); FillSvg(graphics, PatreonPath, m, color); }
                    }
                    else if (glyph == ButtonGlyph.Chat)
                    {
                        // Discord: the logo spans x 2.6-22.4, y 4-19.25 -> 18 px wide at 100 %, centred.
                        float u = 18F * s / 19.8F;
                        using (var m = new Matrix()) { m.Translate(x - 12.5F * u, y - 11.6F * u); m.Scale(u, u); FillSvg(graphics, DiscordPath, m, color); }
                    }
                    else if (glyph == ButtonGlyph.Coffee)
                    {
                        // Ko-fi-style mug: a filled cup in `color` with a thick handle and a red heart on it.
                        x -= 1.2F * s; y += 0.6F * s;
                        using (var cup = new GraphicsPath())
                        {
                            float l = x - 6.5F * s, r = x + 4.5F * s, t = y - 5F * s, b = y + 5.5F * s, round = 3.6F * s;
                            cup.AddLine(l, t, r, t);
                            cup.AddLine(r, t, r, b - round);
                            cup.AddArc(r - round * 2, b - round * 2, round * 2, round * 2, 0, 90);
                            cup.AddLine(r - round, b, l + round, b);
                            cup.AddArc(l, b - round * 2, round * 2, round * 2, 90, 90);
                            cup.CloseFigure();
                            graphics.FillPath(brush, cup);
                        }
                        using (var handle = new Pen(color, 2.2F * s)) graphics.DrawArc(handle, x + 2.2F * s, y - 3.6F * s, 5.6F * s, 5.6F * s, -90, 180);
                        float hx = x - 1F * s, hy = y + 0.4F * s, w = 6.2F * s, h = 5.6F * s;
                        using (var heart = new GraphicsPath())
                        using (var red = new SolidBrush(KofiRed))
                        {
                            PointF tip = new PointF(hx, hy + h / 2);
                            heart.AddBezier(tip, new PointF(hx - w * 0.62F, hy + h * 0.02F), new PointF(hx - w * 0.5F, hy - h * 0.62F), new PointF(hx, hy - h * 0.22F));
                            heart.AddBezier(new PointF(hx, hy - h * 0.22F), new PointF(hx + w * 0.5F, hy - h * 0.62F), new PointF(hx + w * 0.62F, hy + h * 0.02F), tip);
                            heart.CloseFigure();
                            graphics.FillPath(red, heart);
                        }
                    }
                    else if (glyph == ButtonGlyph.Nexus)
                    {
                        // 162 units -> 18 px at 100 %, centred; in the emblem's own colours (`color` is not used).
                        float u = 18F * s / 162F;
                        using (var m = new Matrix())
                        {
                            m.Translate(x - 81 * u, y - 81 * u); m.Scale(u, u);
                            Color orange = Color.FromArgb(0xF9, 0x73, 0x16), disc = Color.FromArgb(0xFB, 0x92, 0x3C), white = Color.FromArgb(0xF4, 0xF4, 0xF5);
                            foreach (string d in new[] { NexusOrangeA, NexusOrangeB, NexusOrangeC, NexusOrangeD }) FillSvg(graphics, d, m, orange);
                            using (var circle = new GraphicsPath()) using (var b = new SolidBrush(disc)) { circle.AddEllipse(81.4F - 60.5F, 80.8F - 60.5F, 121F, 121F); circle.Transform(m); graphics.FillPath(b, circle); }
                            foreach (string d in new[] { NexusWhiteA, NexusWhiteB, NexusWhiteC, NexusWhiteD }) FillSvg(graphics, d, m, white);
                        }
                    }
                    else if (glyph == ButtonGlyph.ColorSync)
                    {
                        // The site's own favicon (colorsync-sf6.pages.dev/data/favicon.svg, 32 units -> 16 px): a green
                        // outlined square and an overlapping blue square, in their own colours (`color` is not used).
                        float u = s / 2F, ox = x - 16 * u, oy = y - 16 * u;
                        using (var green = new Pen(Color.FromArgb(0x66, 0xC6, 0x9A), 2.5F * u)) graphics.DrawRectangle(green, ox + 1.25F * u, oy + 1.25F * u, 22.5F * u, 22.5F * u);
                        using (var blue = new SolidBrush(Color.FromArgb(0x7E, 0xA7, 0xFF))) graphics.FillRectangle(blue, ox + 8 * u, oy + 8 * u, 24 * u, 24 * u);
                    }
                }
            }
            finally { graphics.SmoothingMode = previous; }
        }
        private static void FillSvg(Graphics graphics, string d, Matrix transform, Color color)
        {
            using (GraphicsPath path = SvgPath.Parse(d)) using (var brush = new SolidBrush(color)) { path.Transform(transform); graphics.FillPath(brush, path); }
        }
    }

    // A small SVG path-data reader (M L H V C S Q T A Z, absolute and relative) for the bundled icon paths.
    internal static class SvgPath
    {
        internal static GraphicsPath Parse(string d)
        {
            var path = new GraphicsPath(FillMode.Winding);
            int i = 0; char command = 'M';
            PointF current = PointF.Empty, start = PointF.Empty, lastControl = PointF.Empty; char lastCommand = ' ';
            while (true)
            {
                SkipSeparators(d, ref i);
                if (i >= d.Length) break;
                if (Char.IsLetter(d[i])) command = d[i++];
                else if (command == 'M') command = 'L'; else if (command == 'm') command = 'l';   // implicit lineto after moveto
                bool rel = Char.IsLower(command);
                float ox = rel ? current.X : 0, oy = rel ? current.Y : 0;
                switch (Char.ToUpperInvariant(command))
                {
                    case 'M':
                        current = new PointF(ox + Number(d, ref i), oy + Number(d, ref i)); start = current; path.StartFigure(); break;
                    case 'L': { var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i)); path.AddLine(current, p); current = p; break; }
                    case 'H': { var p = new PointF(ox + Number(d, ref i), current.Y); path.AddLine(current, p); current = p; break; }
                    case 'V': { var p = new PointF(current.X, oy + Number(d, ref i)); path.AddLine(current, p); current = p; break; }
                    case 'C':
                    {
                        var c1 = new PointF(ox + Number(d, ref i), oy + Number(d, ref i)); var c2 = new PointF(ox + Number(d, ref i), oy + Number(d, ref i)); var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        path.AddBezier(current, c1, c2, p); lastControl = c2; current = p; break;
                    }
                    case 'S':
                    {
                        bool smooth = "CcSs".IndexOf(lastCommand) >= 0;
                        var c1 = smooth ? new PointF(2 * current.X - lastControl.X, 2 * current.Y - lastControl.Y) : current;
                        var c2 = new PointF(ox + Number(d, ref i), oy + Number(d, ref i)); var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        path.AddBezier(current, c1, c2, p); lastControl = c2; current = p; break;
                    }
                    case 'Q':
                    {
                        var q = new PointF(ox + Number(d, ref i), oy + Number(d, ref i)); var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        AddQuad(path, current, q, p); lastControl = q; current = p; break;
                    }
                    case 'T':
                    {
                        bool smooth = "QqTt".IndexOf(lastCommand) >= 0;
                        var q = smooth ? new PointF(2 * current.X - lastControl.X, 2 * current.Y - lastControl.Y) : current;
                        var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        AddQuad(path, current, q, p); lastControl = q; current = p; break;
                    }
                    case 'A':
                    {
                        float rx = Number(d, ref i), ry = Number(d, ref i), rotation = Number(d, ref i);
                        bool large = Flag(d, ref i), sweep = Flag(d, ref i);
                        var p = new PointF(ox + Number(d, ref i), oy + Number(d, ref i));
                        AddArc(path, current, p, rx, ry, rotation, large, sweep); current = p; break;
                    }
                    case 'Z': path.CloseFigure(); current = start; break;
                    default: throw new FormatException("Unsupported SVG path command '" + command + "'.");
                }
                lastCommand = command;
            }
            return path;
        }
        private static void AddQuad(GraphicsPath path, PointF p0, PointF q, PointF p)
        {
            path.AddBezier(p0, new PointF(p0.X + 2F / 3 * (q.X - p0.X), p0.Y + 2F / 3 * (q.Y - p0.Y)), new PointF(p.X + 2F / 3 * (q.X - p.X), p.Y + 2F / 3 * (q.Y - p.Y)), p);
        }
        // SVG endpoint arc -> centre form (SVG 1.1 F.6.5), drawn as cubic Béziers of at most 90° each.
        private static void AddArc(GraphicsPath path, PointF p0, PointF p1, float rx, float ry, float rotationDeg, bool large, bool sweep)
        {
            if (rx == 0 || ry == 0) { path.AddLine(p0, p1); return; }
            double phi = rotationDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (p0.X - p1.X) / 2.0, dy = (p0.Y - p1.Y) / 2.0;
            double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
            double arx = Math.Abs(rx), ary = Math.Abs(ry);
            double lambda = x1 * x1 / (arx * arx) + y1 * y1 / (ary * ary);
            if (lambda > 1) { arx *= Math.Sqrt(lambda); ary *= Math.Sqrt(lambda); }
            double num = arx * arx * ary * ary - arx * arx * y1 * y1 - ary * ary * x1 * x1, den = arx * arx * y1 * y1 + ary * ary * x1 * x1;
            double coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, num / den));
            double cx1 = coef * arx * y1 / ary, cy1 = -coef * ary * x1 / arx;
            double cx = cos * cx1 - sin * cy1 + (p0.X + p1.X) / 2.0, cy = sin * cx1 + cos * cy1 + (p0.Y + p1.Y) / 2.0;
            double theta1 = Angle(1, 0, (x1 - cx1) / arx, (y1 - cy1) / ary);
            double delta = Angle((x1 - cx1) / arx, (y1 - cy1) / ary, (-x1 - cx1) / arx, (-y1 - cy1) / ary);
            if (!sweep && delta > 0) delta -= 2 * Math.PI; else if (sweep && delta < 0) delta += 2 * Math.PI;
            int segments = (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2));
            double step = delta / segments, t = 4.0 / 3 * Math.Tan(step / 4);
            PointF from = p0;
            for (int k = 0; k < segments; k++)
            {
                double a1 = theta1 + k * step, a2 = a1 + step;
                Func<double, double, PointF> point = delegate(double ex, double ey) { return new PointF((float)(cx + cos * arx * ex - sin * ary * ey), (float)(cy + sin * arx * ex + cos * ary * ey)); };
                PointF c1 = point(Math.Cos(a1) - t * Math.Sin(a1), Math.Sin(a1) + t * Math.Cos(a1));
                PointF c2 = point(Math.Cos(a2) + t * Math.Sin(a2), Math.Sin(a2) - t * Math.Cos(a2));
                PointF to = k == segments - 1 ? p1 : point(Math.Cos(a2), Math.Sin(a2));
                path.AddBezier(from, c1, c2, to); from = to;
            }
        }
        private static double Angle(double ux, double uy, double vx, double vy)
        {
            double a = Math.Acos(Math.Max(-1, Math.Min(1, (ux * vx + uy * vy) / (Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy)))));
            return ux * vy - uy * vx < 0 ? -a : a;
        }
        private static void SkipSeparators(string d, ref int i) { while (i < d.Length && (Char.IsWhiteSpace(d[i]) || d[i] == ',')) i++; }
        // Arc flags are single 0/1 characters and may touch the next number ("0016 8").
        private static bool Flag(string d, ref int i)
        {
            SkipSeparators(d, ref i);
            if (i >= d.Length || (d[i] != '0' && d[i] != '1')) throw new FormatException("Bad SVG arc flag at " + i + ".");
            return d[i++] == '1';
        }
        // One number: sign, digits, one dot, exponent. A second dot starts the next number (".4.07" = 0.4, 0.07).
        private static float Number(string d, ref int i)
        {
            SkipSeparators(d, ref i);
            int begin = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            bool dot = false;
            while (i < d.Length && (Char.IsDigit(d[i]) || (d[i] == '.' && !dot))) { if (d[i] == '.') dot = true; i++; }
            if (i < d.Length && (d[i] == 'e' || d[i] == 'E')) { i++; if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++; while (i < d.Length && Char.IsDigit(d[i])) i++; }
            if (i == begin) throw new FormatException("Bad SVG number at " + i + ".");
            return Single.Parse(d.Substring(begin, i - begin), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }
}
