using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace musicpresense
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
        /// Builds a small audio quality icon: a tuning slider / equalizer glyph.
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
    }
}