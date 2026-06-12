using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace AndroidMusicPresenceLink
{
    public partial class MediaPlayerWindow
    {
        /// <summary>
        /// Returns whether the dark theme is currently active. Reads from the live
        /// ThemeBackgroundBrush resource because the dark-mode toggle only calls
        /// ApplyTheme, leaving App.Config.UseDarkMode stale until the next save.
        /// </summary>
        private static bool IsDarkThemeActive()
        {
            if (Application.Current?.Resources["ThemeBackgroundBrush"] is SolidColorBrush bg)
            {
                // Use luminance midpoint: anything darker than mid-grey is "dark".
                var c = bg.Color;
                int luma = (c.R * 299 + c.G * 587 + c.B * 114) / 1000;
                return luma < 128;
            }
            return App.Config?.UseDarkMode ?? true;
        }
        private Brush ResolveIconBrush()
        {
            // While a song is playing the player background is always a dark gradient,
            // so icons must always be white regardless of the app theme.
            // When idle, the background follows the theme: white in light mode (need
            // black icons), near-black in dark mode (need white icons).
            if (_hasSong)
                return Brushes.White;

            return IsDarkThemeActive() ? Brushes.White : Brushes.Black;
        }

        /// <summary>
        /// Returns the foreground brush that matches the popup's theme-aware
        /// background. Reads ThemeControlForegroundBrush from app resources, with a
        /// luminance-based fallback so we never end up with black-on-black.
        /// </summary>
        private Brush ResolveMenuForegroundBrush()
        {
            if (Application.Current?.Resources["ThemeControlForegroundBrush"] is Brush b)
                return b;
            return IsDarkThemeActive() ? Brushes.White : Brushes.Black;
        }

        /// <summary>
        /// Volume glyph levels mapped from the absolute (0..1) volume.
        /// </summary>
        private enum VolumeIconLevel { Muted, Low, Medium, High }
        private static VolumeIconLevel LevelFromVolume(float v)
        {
            if (v <= 0.001f) return VolumeIconLevel.Muted;
            if (v < 0.34f) return VolumeIconLevel.Low;
            if (v < 0.67f) return VolumeIconLevel.Medium;
            return VolumeIconLevel.High;
        }


        private static Viewbox BuildPreviousIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var bar = new Rectangle
            {
                Width = 2.4,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(bar, 2);
            Canvas.SetTop(bar, 4);

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(15, 4),
                    new Point(6, 10),
                    new Point(15, 16)
                }
            };

            canvas.Children.Add(bar);
            canvas.Children.Add(triangle);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }
        private static Viewbox BuildPlayIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(6, 4),
                    new Point(15, 10),
                    new Point(6, 16)
                }
            };

            canvas.Children.Add(triangle);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }
        private static Viewbox BuildPauseIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var leftBar = new Rectangle
            {
                Width = 3,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(leftBar, 5);
            Canvas.SetTop(leftBar, 4);

            var rightBar = new Rectangle
            {
                Width = 3,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(rightBar, 12);
            Canvas.SetTop(rightBar, 4);

            canvas.Children.Add(leftBar);
            canvas.Children.Add(rightBar);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }
        private static Viewbox BuildNextIcon(Brush brush, double size = 20)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var triangle = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(5, 4),
                    new Point(14, 10),
                    new Point(5, 16)
                }
            };

            var bar = new Rectangle
            {
                Width = 2.4,
                Height = 12,
                Fill = brush
            };
            Canvas.SetLeft(bar, 16);
            Canvas.SetTop(bar, 4);

            canvas.Children.Add(triangle);
            canvas.Children.Add(bar);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }
        private static Viewbox BuildRevealSettingsArrowIcon(Brush brush)
        {
            var canvas = new Canvas { Width = 14, Height = 20 };

            var chevron = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(3, 3),
                    new Point(11, 10),
                    new Point(3, 17),
                    new Point(6, 17),
                    new Point(14, 10),
                    new Point(6, 3)
                }
            };

            canvas.Children.Add(chevron);
            return new Viewbox { Width = 14, Height = 20, Child = canvas };
        }

        /// <summary>
        /// Left-pointing chevron: shown on the collapse button when the settings pane is open.
        /// Mirror of <see cref="BuildRevealSettingsArrowIcon"/>.
        /// </summary>
        private static Viewbox BuildCollapseSettingsArrowIcon(Brush brush)
        {
            var canvas = new Canvas { Width = 14, Height = 20 };

            var chevron = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(11, 3),
                    new Point(3, 10),
                    new Point(11, 17),
                    new Point(8, 17),
                    new Point(0, 10),
                    new Point(8, 3)
                }
            };

            canvas.Children.Add(chevron);
            return new Viewbox { Width = 14, Height = 20, Child = canvas };
        }

        /// <summary>
        /// Builds the speaker glyph with 0, 1, or 2 sound-wave arcs depending on level.
        /// Muted gets a small slash through the speaker.
        /// </summary>
        private static Viewbox BuildVolumeIcon(Brush brush, double size = 20, VolumeIconLevel level = VolumeIconLevel.High)
        {
            // Canvas is 22 wide (vs 20) so the outermost High-level arc has room
            // without clipping. Viewbox scales the whole thing to the requested size.
            var canvas = new Canvas { Width = 22, Height = 20 };

            // Speaker body: small rectangle (back) + triangle horn projecting right.
            var speaker = new Polygon
            {
                Fill = brush,
                Points = new PointCollection
                {
                    new Point(2, 7.5),
                    new Point(6, 7.5),
                    new Point(11, 3),
                    new Point(11, 17),
                    new Point(6, 12.5),
                    new Point(2, 12.5)
                }
            };
            canvas.Children.Add(speaker);

            // Inner arc (Low/Medium/High).
            if (level == VolumeIconLevel.Low || level == VolumeIconLevel.Medium || level == VolumeIconLevel.High)
            {
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M13.5,8 Q15.5,10 13.5,12")
                });
            }

            // Middle arc (Medium/High).
            if (level == VolumeIconLevel.Medium || level == VolumeIconLevel.High)
            {
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M15.5,6.5 Q18,10 15.5,13.5")
                });
            }

            // Outer arc (High only).
            if (level == VolumeIconLevel.High)
            {
                canvas.Children.Add(new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Data = Geometry.Parse("M17.5,5 Q20.5,10 17.5,15")
                });
            }

            // Muted: draw a diagonal slash across the speaker.
            if (level == VolumeIconLevel.Muted)
            {
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 13,
                    Y1 = 6,
                    X2 = 18,
                    Y2 = 14,
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildLyricsIcon(Brush brush, double size = 20, bool active = false)
        {
            // Stack of horizontal text lines, with one line indented to suggest lyric text.
            var canvas = new Canvas { Width = 20, Height = 20 };

            if (active)
            {
                // Rounded background pill behind the lines indicates active state.
                var bg = new Rectangle
                {
                    Width = 18,
                    Height = 18,
                    Fill = brush,
                    Opacity = 0.18,
                    RadiusX = 4,
                    RadiusY = 4
                };
                Canvas.SetLeft(bg, 1);
                Canvas.SetTop(bg, 1);
                canvas.Children.Add(bg);
            }

            void AddLine(double x, double y, double width)
            {
                var line = new Rectangle
                {
                    Width = width,
                    Height = 2,
                    Fill = brush,
                    RadiusX = 1,
                    RadiusY = 1
                };
                Canvas.SetLeft(line, x);
                Canvas.SetTop(line, y);
                canvas.Children.Add(line);
            }

            AddLine(3, 4, 14);
            AddLine(3, 8.5, 10);
            AddLine(3, 13, 13);
            AddLine(3, 17.5, 8);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        /// <summary>
        /// Builds a seek icon: a triangle (direction) plus a small number label.
        /// seconds is e.g. -30, 10, 30 etc.
        /// </summary>
        private static Viewbox BuildSeekIcon(Brush brush, int seconds, double size = 22)
        {
            bool forward = seconds > 0;
            var canvas = new Canvas { Width = 28, Height = 20 };

            // Arrow triangle
            var tri = new Polygon
            {
                Fill = brush,
                Points = forward
                    ? new PointCollection { new Point(4, 4), new Point(12, 10), new Point(4, 16) }
                    : new PointCollection { new Point(12, 4), new Point(4, 10), new Point(12, 16) }
            };
            canvas.Children.Add(tri);

            // Second triangle (double chevron feel)
            var tri2 = new Polygon
            {
                Fill = brush,
                Opacity = 0.6,
                Points = forward
                    ? new PointCollection { new Point(10, 4), new Point(18, 10), new Point(10, 16) }
                    : new PointCollection { new Point(18, 4), new Point(10, 10), new Point(18, 16) }
            };
            canvas.Children.Add(tri2);

            return new Viewbox { Width = size, Height = size * 20 / 28, Child = canvas };
        }

        /// <summary>
        /// Builds an audio-link icon: a waveform/chain link that is solid when active,
        /// dimmed/crossed when inactive.
        /// </summary>
        private static Viewbox BuildAudioLinkIcon(Brush brush, bool active, double size = 22)
        {
            var canvas = new Canvas { Width = 22, Height = 20 };

            // Draw two chain-link ovals
            void AddLink(double cx, double cy)
            {
                var e1 = new System.Windows.Shapes.Path
                {
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    Data = Geometry.Parse($"M {cx - 4},{cy} A 4,3 0 1 1 {cx + 4},{cy} A 4,3 0 1 1 {cx - 4},{cy}")
                };
                canvas.Children.Add(e1);
            }

            AddLink(7, 10);
            AddLink(15, 10);

            // Connecting bar
            var bar = new Rectangle { Width = 4, Height = 2, Fill = brush };
            Canvas.SetLeft(bar, 9);
            Canvas.SetTop(bar, 9);
            canvas.Children.Add(bar);

            // If inactive: draw a diagonal slash
            if (!active)
            {
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = 3,
                    Y1 = 3,
                    X2 = 19,
                    Y2 = 17,
                    Stroke = brush,
                    StrokeThickness = 1.8,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round,
                    Opacity = 0.7
                });
            }

            var vb = new Viewbox { Width = size, Height = size, Child = canvas };
            if (!active) vb.Opacity = 0.45;
            return vb;
        }

        /// <summary>
        /// Builds the fallback/custom audio quality icon: a tuning slider / equalizer glyph.
        /// Three vertical bars of varying heights with a small dot indicating the
        /// "knob" position on each.
        /// </summary>
        private static Viewbox BuildAudioQualityIcon(Brush brush, double size = 18)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            // Three vertical track lines.
            void AddTrack(double x)
            {
                var track = new Rectangle
                {
                    Width = 1.6,
                    Height = 14,
                    Fill = brush,
                    Opacity = 0.5,
                    RadiusX = 0.8,
                    RadiusY = 0.8
                };
                Canvas.SetLeft(track, x - 0.8);
                Canvas.SetTop(track, 3);
                canvas.Children.Add(track);
            }

            // Solid knob marker on each track.
            void AddKnob(double cx, double cy)
            {
                var knob = new Rectangle
                {
                    Width = 6,
                    Height = 3,
                    Fill = brush,
                    RadiusX = 1.5,
                    RadiusY = 1.5
                };
                Canvas.SetLeft(knob, cx - 3);
                Canvas.SetTop(knob, cy - 1.5);
                canvas.Children.Add(knob);
            }

            AddTrack(5);
            AddTrack(10);
            AddTrack(15);

            AddKnob(5, 13);
            AddKnob(10, 7);
            AddKnob(15, 10);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        /// <summary>
        /// Builds a question-mark "help" glyph inside a thin circle.
        /// </summary>
        private static Viewbox BuildHelpIcon(Brush brush, double size = 18)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            var ring = new System.Windows.Shapes.Ellipse
            {
                Width = 16,
                Height = 16,
                Stroke = brush,
                StrokeThickness = 1.4,
                Fill = Brushes.Transparent,
                Opacity = 0.9
            };
            Canvas.SetLeft(ring, 2);
            Canvas.SetTop(ring, 2);
            canvas.Children.Add(ring);

            // The "?" mark, drawn as a path so it scales cleanly.
            var qmark = new System.Windows.Shapes.Path
            {
                Stroke = brush,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Data = Geometry.Parse("M 7.5,8 C 7.5,6 8.7,5 10,5 C 11.5,5 12.5,6 12.5,7.5 C 12.5,9 10.5,9.5 10,11 L 10,12.2")
            };
            canvas.Children.Add(qmark);

            // Dot under the question mark
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 2,
                Height = 2,
                Fill = brush
            };
            Canvas.SetLeft(dot, 9);
            Canvas.SetTop(dot, 14);
            canvas.Children.Add(dot);

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        /// <summary>
        /// Builds a fullscreen toggle icon: four corner brackets pointing outward
        /// when entering fullscreen, inward when already in fullscreen.
        /// </summary>
        private static Viewbox BuildFullscreenIcon(Brush brush, bool active, double size = 18)
        {
            var canvas = new Canvas { Width = 20, Height = 20 };

            // Each corner is two short strokes meeting at a right angle.
            // When `active` (in fullscreen), the brackets point inward (collapse glyph);
            // otherwise they point outward (expand glyph).
            void AddCorner(double cx, double cy, int dx, int dy)
            {
                // dx/dy in {-1, +1} indicate direction the corner opens toward.
                double len = 5;
                // Outer point of the L
                double ox = cx;
                double oy = cy;
                // Two endpoints making the L
                double ex1 = cx + len * dx;
                double ey1 = cy;
                double ex2 = cx;
                double ey2 = cy + len * dy;

                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = ox,
                    Y1 = oy,
                    X2 = ex1,
                    Y2 = ey1,
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
                canvas.Children.Add(new System.Windows.Shapes.Line
                {
                    X1 = ox,
                    Y1 = oy,
                    X2 = ex2,
                    Y2 = ey2,
                    Stroke = brush,
                    StrokeThickness = 1.6,
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }

            // outward (expand): brackets at outer edges, opening toward center
            // inward  (collapse): brackets near center, opening toward edges
            if (!active)
            {
                AddCorner(3, 3, +1, +1);    // top-left, opens down-right
                AddCorner(17, 3, -1, +1);   // top-right
                AddCorner(3, 17, +1, -1);   // bottom-left
                AddCorner(17, 17, -1, -1);  // bottom-right
            }
            else
            {
                AddCorner(8, 8, -1, -1);    // pointing toward top-left
                AddCorner(12, 8, +1, -1);   // top-right
                AddCorner(8, 12, -1, +1);   // bottom-left
                AddCorner(12, 12, +1, +1);  // bottom-right
            }

            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        /// <summary>
        /// Builds an "always on top" pin icon: a thumbtack viewed from the side.
        /// Filled head when active, outlined when inactive.
        /// </summary>
        private static Viewbox BuildAlwaysOnTopIcon(Brush brush, bool active, double size = 14)
        {
            var canvas = new Canvas { Width = 18, Height = 18 };

            // The pin head (filled circle on top)
            var head = new System.Windows.Shapes.Ellipse
            {
                Width = 8,
                Height = 8,
                Stroke = brush,
                StrokeThickness = 1.4,
                Fill = active ? brush : Brushes.Transparent
            };
            Canvas.SetLeft(head, 5);
            Canvas.SetTop(head, 1);
            canvas.Children.Add(head);

            // Pin shaft
            var shaft = new System.Windows.Shapes.Line
            {
                X1 = 9,
                Y1 = 9,
                X2 = 9,
                Y2 = 16,
                Stroke = brush,
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            canvas.Children.Add(shaft);

            // Two small arrow ticks at the bottom suggesting "stays put / pinned"
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 6,
                Y1 = 13,
                X2 = 9,
                Y2 = 10,
                Stroke = brush,
                StrokeThickness = 1.4,
                Opacity = 0.9
            });
            canvas.Children.Add(new System.Windows.Shapes.Line
            {
                X1 = 12,
                Y1 = 13,
                X2 = 9,
                Y2 = 10,
                Stroke = brush,
                StrokeThickness = 1.4,
                Opacity = 0.9
            });

            var vb = new Viewbox { Width = size, Height = size, Child = canvas };
            if (!active) vb.Opacity = 0.75;
            return vb;
        }

        // ── Connection top-mode icons ─────────────────────────────────────────

        private static Viewbox BuildUsbIcon(Brush brush, double size = 36)
        {
            // USB trident built from SVG path (viewBox 475x228 scaled to ~28x13.5)
            const double W = 32, H = 30, s = 0.059, ox = 1.5, oy = 1;
            var canvas = new Canvas { Width = W, Height = H };
            var path = new System.Windows.Shapes.Path { Fill = brush };
            var pg = new PathGeometry();
            pg.FillRule = FillRule.Nonzero;
            var fig = new PathFigure { StartPoint = new Point(462.836 * s + ox, 114.054 * s + oy), IsClosed = true, IsFilled = true };
            fig.Segments.Add(new LineSegment(new Point(412.799 * s + ox, 85.158 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(412.799 * s + ox, 105.771 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(157.046 * s + ox, 105.771 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(206.844 * s + ox, 53.159 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(211.082 * s + ox, 49.762 * s + oy), new Point(216.627 * s + ox, 47.379 * s + oy), new Point(222.331 * s + ox, 47.247 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(264.153 * s + ox, 47.231 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(267.572 * s + ox, 56.972 * s + oy), new Point(276.756 * s + ox, 64.003 * s + oy), new Point(287.674 * s + ox, 64.003 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(301.486 * s + ox, 64.003 * s + oy), new Point(312.695 * s + ox, 52.795 * s + oy), new Point(312.695 * s + ox, 38.978 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(312.695 * s + ox, 25.155 * s + oy), new Point(301.487 * s + ox, 13.951 * s + oy), new Point(287.674 * s + ox, 13.951 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(276.756 * s + ox, 13.951 * s + oy), new Point(267.572 * s + ox, 20.978 * s + oy), new Point(264.153 * s + ox, 30.711 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(222.821 * s + ox, 30.704 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(211.619 * s + ox, 30.704 * s + oy), new Point(199.881 * s + ox, 36.85 * s + oy), new Point(192.41 * s + ox, 44.055 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(139.564 * s + ox, 99.873 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(135.335 * s + ox, 103.265 * s + oy), new Point(129.793 * s + ox, 105.633 * s + oy), new Point(124.093 * s + ox, 105.769 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(95.161 * s + ox, 105.769 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(91.326 * s + ox, 86.656 * s + oy), new Point(74.448 * s + ox, 72.256 * s + oy), new Point(54.202 * s + ox, 72.256 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(31.119 * s + ox, 72.256 * s + oy), new Point(12.408 * s + ox, 90.967 * s + oy), new Point(12.408 * s + ox, 114.043 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(12.408 * s + ox, 137.126 * s + oy), new Point(31.119 * s + ox, 155.838 * s + oy), new Point(54.202 * s + ox, 155.838 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(74.452 * s + ox, 155.838 * s + oy), new Point(91.33 * s + ox, 141.426 * s + oy), new Point(95.165 * s + ox, 122.297 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(186.681 * s + ox, 122.297 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(192.37 * s + ox, 122.442 * s + oy), new Point(197.905 * s + ox, 124.813 * s + oy), new Point(202.13 * s + ox, 128.209 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(254.957 * s + ox, 184.021 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(262.432 * s + ox, 191.229 * s + oy), new Point(274.175 * s + ox, 197.371 * s + oy), new Point(285.379 * s + ox, 197.371 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(325.211 * s + ox, 197.362 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(325.211 * s + ox, 214.139 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(375.261 * s + ox, 214.139 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(375.261 * s + ox, 164.094 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(325.211 * s + ox, 164.094 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(325.211 * s + ox, 180.849 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(284.891 * s + ox, 180.830 * s + oy), true));
            fig.Segments.Add(new BezierSegment(new Point(279.186 * s + ox, 180.699 * s + oy), new Point(273.635 * s + ox, 178.319 * s + oy), new Point(269.399 * s + ox, 174.922 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(219.59 * s + ox, 122.3 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(412.799 * s + ox, 122.3 * s + oy), true));
            fig.Segments.Add(new LineSegment(new Point(412.799 * s + ox, 142.946 * s + oy), true));
            pg.Figures.Add(fig);
            path.Data = pg;
            canvas.Children.Add(path);
            var tb = new TextBlock { Text = "USB", FontSize = 9.5, Foreground = brush, FontWeight = FontWeights.SemiBold, Width = W, TextAlignment = TextAlignment.Center };
            canvas.Children.Add(tb); Canvas.SetLeft(tb, 0); Canvas.SetTop(tb, 16);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static void AddWifiWaves(Canvas canvas, Brush brush, double cx, double baseY, bool slashed = false)
        {
            double[] radii = { 5, 9, 13 };
            for (int i = 0; i < radii.Length; i++)
            {
                double r = radii[i];
                double startAngle = 210 * Math.PI / 180;
                double endAngle = 330 * Math.PI / 180;
                double x1 = cx + r * Math.Cos(startAngle);
                double y1 = baseY + r * Math.Sin(startAngle);
                double x2 = cx + r * Math.Cos(endAngle);
                double y2 = baseY + r * Math.Sin(endAngle);
                var fig = new PathFigure { StartPoint = new Point(x1, y1) };
                fig.Segments.Add(new ArcSegment(new Point(x2, y2), new Size(r, r), 0, false, SweepDirection.Clockwise, true));
                var geo = new PathGeometry();
                geo.Figures.Add(fig);
                canvas.Children.Add(new System.Windows.Shapes.Path { Data = geo, Stroke = brush, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Fill = Brushes.Transparent });
            }
            // Dot sits at the arc origin point
            var wdot = new System.Windows.Shapes.Ellipse { Width = 4, Height = 4, Fill = brush };
            canvas.Children.Add(wdot); Canvas.SetLeft(wdot, cx - 2); Canvas.SetTop(wdot, baseY - 1);
            if (slashed)
                canvas.Children.Add(new System.Windows.Shapes.Line { X1 = cx - 13, Y1 = baseY + 12, X2 = cx + 13, Y2 = baseY - 12, Stroke = brush, StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Opacity = 0.9 });
        }

        private static Viewbox BuildTcpIcon(Brush brush, double size = 36)
        {
            const double W = 32, H = 36, cx = W / 2;
            var canvas = new Canvas { Width = W, Height = H };
            AddWifiWaves(canvas, brush, cx: cx, baseY: 14);
            var tb = new TextBlock { Text = "TCP", FontSize = 8.5, Foreground = brush, FontWeight = FontWeights.SemiBold, Width = W, TextAlignment = TextAlignment.Center };
            canvas.Children.Add(tb); Canvas.SetLeft(tb, 0); Canvas.SetTop(tb, 22);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildWdIcon(Brush brush, double size = 36)
        {
            const double W = 32, H = 36, cx = W / 2;
            var canvas = new Canvas { Width = W, Height = H };
            AddWifiWaves(canvas, brush, cx: cx, baseY: 14);
            var tb = new TextBlock { Text = "WD", FontSize = 8.5, Foreground = brush, FontWeight = FontWeights.SemiBold, Width = W, TextAlignment = TextAlignment.Center };
            canvas.Children.Add(tb); Canvas.SetLeft(tb, 0); Canvas.SetTop(tb, 22);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildPortLostIcon(Brush brush, bool isWd = false, double size = 36)
        {
            const double W = 32, H = 36, cx = W / 2;
            var canvas = new Canvas { Width = W, Height = H };
            AddWifiWaves(canvas, brush, cx: cx, baseY: 14, slashed: true);
            var label = isWd ? "WD" : "TCP";
            var tb = new TextBlock { Text = label, FontSize = 8.5, Foreground = brush, FontWeight = FontWeights.SemiBold, Opacity = 0.7, Width = W, TextAlignment = TextAlignment.Center };
            canvas.Children.Add(tb); Canvas.SetLeft(tb, 0); Canvas.SetTop(tb, 22);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

        private static Viewbox BuildNoConnectionIcon(Brush brush, double size = 36)
        {
            const double W = 32, H = 36, cx = W / 2;
            var canvas = new Canvas { Width = W, Height = H };
            var tri = new PathFigure { StartPoint = new Point(cx, 2) };
            tri.Segments.Add(new LineSegment(new Point(cx + 12, 24), true));
            tri.Segments.Add(new LineSegment(new Point(cx - 12, 24), true));
            tri.Segments.Add(new LineSegment(new Point(cx, 2), true));
            var triGeo = new PathGeometry();
            triGeo.Figures.Add(tri);
            canvas.Children.Add(new System.Windows.Shapes.Path { Data = triGeo, Stroke = brush, StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round, Fill = Brushes.Transparent });
            canvas.Children.Add(new System.Windows.Shapes.Line { X1 = cx, Y1 = 10, X2 = cx, Y2 = 17, Stroke = brush, StrokeThickness = 2, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round });
            var excDot = new System.Windows.Shapes.Ellipse { Width = 2.5, Height = 2.5, Fill = brush };
            canvas.Children.Add(excDot); Canvas.SetLeft(excDot, cx - 1.25); Canvas.SetTop(excDot, 19);
            return new Viewbox { Width = size, Height = size, Child = canvas };
        }

    }
}