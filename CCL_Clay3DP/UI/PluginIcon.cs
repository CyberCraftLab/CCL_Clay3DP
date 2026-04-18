// Copyright 2026 CyberCraft Lab, OTH Regensburg, Prof. Christophe Barlieb
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using Rhino;

namespace CCL_Clay3DP.UI
{
    /// <summary>
    /// Loads the CyberCraft logo (embedded as CCL_Clay3DP.CCLogo.png)
    /// and returns it as a System.Drawing.Icon built from a proper ICO-format
    /// stream. This avoids the GetHicon() handle-lifecycle quirks that can
    /// prevent the panel tab image from rendering.
    /// </summary>
    public static class PluginIcon
    {
        // Keep references alive for the plugin's lifetime.
        private static Icon _icon;
        private static MemoryStream _iconStream; // keep alive so Icon's stream isn't disposed

        private const string ResourceName = "CCL_Clay3DP.CCLogo.png";

        public static Icon Create(int size = 32)
        {
            if (_icon != null) return _icon;

            try
            {
                // Load the source PNG and resize to the requested icon size
                using (var source = LoadSourceBitmap() ?? FallbackBitmap(size))
                using (var resized = new Bitmap(size, size, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(resized))
                    {
                        g.SmoothingMode = SmoothingMode.AntiAlias;
                        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g.Clear(Color.Transparent);
                        g.DrawImage(source, new Rectangle(0, 0, size, size));
                    }

                    // Build an ICO file in memory that wraps the PNG bytes
                    _iconStream = BuildIcoStreamFromBitmap(resized, size);
                    _iconStream.Position = 0;
                    _icon = new Icon(_iconStream);
                    return _icon;
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"CCL_Clay3DP: icon build failed: {ex.Message}");
                return null;
            }
        }

        private static Bitmap LoadSourceBitmap()
        {
            try
            {
                var asm = Assembly.GetExecutingAssembly();
                using (var stream = asm.GetManifestResourceStream(ResourceName))
                {
                    if (stream == null)
                    {
                        RhinoApp.WriteLine($"CCL_Clay3DP: resource '{ResourceName}' not found.");
                        return null;
                    }
                    return new Bitmap(stream);
                }
            }
            catch (Exception ex)
            {
                RhinoApp.WriteLine($"CCL_Clay3DP: could not read logo resource: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Construct a Windows ICO file containing a single PNG image.
        /// Modern Windows Icon loaders support PNG-in-ICO natively.
        /// </summary>
        private static MemoryStream BuildIcoStreamFromBitmap(Bitmap bmp, int size)
        {
            // Encode the bitmap as PNG bytes
            byte[] pngBytes;
            using (var pngStream = new MemoryStream())
            {
                bmp.Save(pngStream, ImageFormat.Png);
                pngBytes = pngStream.ToArray();
            }

            var ico = new MemoryStream();
            using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                // ICONDIR (6 bytes)
                w.Write((ushort)0);     // reserved
                w.Write((ushort)1);     // type = 1 (icon)
                w.Write((ushort)1);     // image count

                // ICONDIRENTRY (16 bytes)
                // 0 means 256 for width/height, otherwise the actual size up to 255
                byte dim = (byte)(size >= 256 ? 0 : size);
                w.Write(dim);           // width
                w.Write(dim);           // height
                w.Write((byte)0);       // palette color count (0 for true-color)
                w.Write((byte)0);       // reserved
                w.Write((ushort)1);     // color planes
                w.Write((ushort)32);    // bits per pixel
                w.Write((uint)pngBytes.Length);   // data size
                w.Write((uint)22);                // data offset (6 + 16)

                // Image data (PNG)
                w.Write(pngBytes);
            }
            return ico;
        }

        /// <summary>Fallback bitmap if the embedded resource is missing.</summary>
        private static Bitmap FallbackBitmap(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (var black = new SolidBrush(Color.Black))
                    g.FillEllipse(black, 0, 0, size, size);
                using (var yellow = new SolidBrush(Color.FromArgb(255, 245, 220, 0)))
                    g.FillEllipse(yellow, size * 0.3f, size * 0.3f, size * 0.4f, size * 0.4f);
            }
            return bmp;
        }
    }
}
