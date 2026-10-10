using System;
using System.Collections.Generic;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    public enum DotStyle { Pin, Vessel, Point, LabelOnly }

    /* A point on the 2D map: reference site, vessel or tool vertex. */
    public sealed class MapDot
    {
        public double Lat, Lon;
        public DotStyle Style = DotStyle.Point;
        public ColorF Fill = ColorF.White;
        public string Tooltip;          // shown on hover
        public string Label;            // fixed label, always visible
        public object Tag;              // what it represents (Marker, Vessel...)
        public bool Hidden;
    }

    /* Polyline in lat/lon. It's stored unwrapped in longitude (no ±360° jumps) to paint it
       continuous on each copy of the world, instead of splitting it at the antimeridian as
       Leaflet did. */
    public sealed class MapLine
    {
        public ColorF Color;
        public float Width = 2;
        public bool Dashed;
        public List<LatLon> Pts { get; private set; }
        public double MinLon { get; private set; }
        public double MaxLon { get; private set; }

        public MapLine(IReadOnlyList<LatLon> pts, ColorF color, float width, bool dashed = false)
        {
            Color = color; Width = width; Dashed = dashed;
            SetPoints(pts);
        }

        public void SetPoints(IReadOnlyList<LatLon> pts)
        {
            Pts = Geo.Unwrap(pts);
            double mn = double.MaxValue, mx = double.MinValue;
            foreach (var p in Pts) { if (p.Lon < mn) mn = p.Lon; if (p.Lon > mx) mx = p.Lon; }
            MinLon = mn; MaxLon = mx;
        }

        public static MapLine FromTrack(IReadOnlyList<TrackPoint> pts, ColorF color, float width)
        {
            var ll = new List<LatLon>(pts.Count);
            foreach (var p in pts) ll.Add(new LatLon(p.Lat, p.Lon));
            return new MapLine(ll, color, width);
        }
    }

    /* Large label over a territory (a faction's name): shown if the territory is big enough on
       screen for it to fit. */
    public sealed class MapEtiqueta
    {
        public double Lat, Lon;
        public double RadioM;                   // how big the territory is around the point
        public string Texto;
        public ColorF Color;
    }

    public sealed class MapLayer
    {
        public readonly List<MapLine> Lines = new();
        public readonly List<MapDot> Dots = new();
        public bool Visible = true;

        public void Clear() { Lines.Clear(); Dots.Clear(); }
    }
}
