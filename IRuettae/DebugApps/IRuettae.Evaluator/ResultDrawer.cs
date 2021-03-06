﻿using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using IRuettae.Core.Models;

namespace IRuettae.Evaluator
{
    public class ResultDrawer
    {
        private static readonly Color[] Colors = new[]
        {
            Color.Red, Color.Green, Color.Blue,
            Color.Violet, Color.Brown, Color.Aqua,
            Color.Black, Color.DarkOrange,Color.Gold,
            Color.Olive, Color.SlateGray, Color.Chartreuse,
            Color.DeepPink, Color.MidnightBlue, Color.Tan
        };

        private const int CircleRadius = 10;

        public static void DrawResult(string path, OptimizationResult result, (int x, int y)[] coordinates)
        {

            var img = new Bitmap(4100, 4100);
            var g = Graphics.FromImage(img);
            g.FillRectangle(Brushes.White, 0, 0, img.Width, img.Height);

            var colorIndex = 0;

            var font = new Font("Arial", 24);

            foreach (var route in result.NonEmptyRoutes)
            {
                var pen = new Pen(Colors[colorIndex], 2);
                colorIndex = (colorIndex + 1) % Colors.Length;
                int? lastX = null;
                int? lastY = null;
                foreach (var waypoint in route.Waypoints.OrderBy(wp => wp.StartTime))
                {
                    var (x, y) = coordinates[waypoint.VisitId == -1 ? 0 : waypoint.VisitId + 1];
                    y = img.Height - y;
                    g.DrawEllipse(pen, x - CircleRadius / 2, y - CircleRadius / 2, CircleRadius, CircleRadius);
                    g.DrawString(waypoint.VisitId.ToString(), font, pen.Brush, x, y);
                    if (lastX.HasValue)
                    {
                        g.DrawLine(pen, x, y, lastX.Value, lastY.Value);
                    }

                    lastX = x;
                    lastY = y;
                }
            }

            g.Save();
            img.Save(path + ".gif", ImageFormat.Gif);

        }
    }
}
