using System;
using System.Collections.Generic;
using KerbinMaps.Core;
using KerbinMaps.Gfx;

namespace KerbinMaps.Views
{
    public enum DotStyle { Pin, Vessel, Point, LabelOnly }

    /* Un punto del mapa 2D: sitio de referencia, nave o vértice de una herramienta. */
    public sealed class MapDot
    {
        public double Lat, Lon;
        public DotStyle Style = DotStyle.Point;
        public ColorF Fill = ColorF.White;
        public string Tooltip;          // se enseña al pasar por encima
        public string Label;            // rótulo fijo, siempre visible
        public object Tag;              // lo que representa (Marker, Vessel...)
        public bool Hidden;
    }

    /* Polilínea en lat/lon. Se guarda desenvuelta en longitud (sin saltos de ±360°)
       para pintarla continua en cada copia del mundo, en vez de trocearla por el
       antimeridiano como hacía Leaflet. */
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

    public sealed class MapLayer
    {
        public readonly List<MapLine> Lines = new();
        public readonly List<MapDot> Dots = new();
        public bool Visible = true;

        public void Clear() { Lines.Clear(); Dots.Clear(); }
    }
}
