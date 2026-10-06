using ClassicUO.Assets;
using ClassicUO.Utility.Logging;
using Microsoft.Xna.Framework.Graphics;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ClassicUO.Renderer.MultiMaps
{
    public sealed class MultiMap
    {
        private readonly GraphicsDevice _device;
        private readonly MultiMapLoader _multiMapLoader;

        public MultiMap(MultiMapLoader multiMapLodaer, GraphicsDevice device)
        {
            _multiMapLoader = multiMapLodaer;
            _device = device;
        }

        public SpriteInfo GetMap(int? facet, int width, int height, int startX, int startY, int endX, int endY)
        {
            MultiMapInfo multiMapInfo = facet.HasValue && _multiMapLoader.HasFacet(facet.Value) ?
                _multiMapLoader.LoadFacet(facet.Value, width, height, startX, startY, endX, endY) :
                _multiMapLoader.LoadMap(width, height, startX, startY, endX, endY);

            if (multiMapInfo.Pixels.IsEmpty)
                return default;

            int textureWidth = multiMapInfo.Width;
            int textureHeight = multiMapInfo.Height;

            if (textureWidth <= 0 || textureHeight <= 0 || multiMapInfo.Pixels.Length < (long)textureWidth * textureHeight)
            {
                Log.Warn($"Refusing malformed map pixels: {textureWidth}x{textureHeight} with {multiMapInfo.Pixels.Length} pixels");

                return default;
            }

            uint[] downscaledPixels = null;
            int maxDimension = GraphicsDeviceCapabilities.MaxTextureDimension;

            if (textureWidth > maxDimension || textureHeight > maxDimension)
            {
                float scale = Math.Min((float)maxDimension / textureWidth, (float)maxDimension / textureHeight);
                int downscaledWidth = Math.Max(1, (int)(textureWidth * scale));
                int downscaledHeight = Math.Max(1, (int)(textureHeight * scale));

                Log.Warn($"Map texture {textureWidth}x{textureHeight} exceeds the renderer limit of {maxDimension}; downscaled to {downscaledWidth}x{downscaledHeight}");

                downscaledPixels = Downscale(multiMapInfo.Pixels, textureWidth, textureHeight, downscaledWidth, downscaledHeight);
                textureWidth = downscaledWidth;
                textureHeight = downscaledHeight;
            }

            ReadOnlySpan<uint> pixels = downscaledPixels;
            if (pixels.IsEmpty)
                pixels = multiMapInfo.Pixels;

            var texture = new Texture2D(_device, textureWidth, textureHeight, false, SurfaceFormat.Color);
            unsafe
            {
                fixed (uint* ptr = pixels)
                {
                    texture.SetDataPointerEXT(0, null, (IntPtr)ptr, sizeof(uint) * textureWidth * textureHeight);
                }
            }

            return new SpriteInfo()
            {
                Texture = texture,
                UV = new Microsoft.Xna.Framework.Rectangle(0, 0, textureWidth, textureHeight),
                Center = Microsoft.Xna.Framework.Point.Zero
            };
        }

        /// <summary>
        /// Nearest-neighbor resample. Map pixels are flat hue-colored blocks, so skipping samples is
        /// visually lossless at the scales involved and keeps this allocation-free of intermediate buffers
        /// beyond the destination.
        /// </summary>
        private static uint[] Downscale(ReadOnlySpan<uint> source, int sourceWidth, int sourceHeight, int targetWidth, int targetHeight)
        {
            uint[] result = new uint[targetWidth * targetHeight];

            for (int y = 0; y < targetHeight; y++)
            {
                int sourceRow = (int)((long)y * sourceHeight / targetHeight) * sourceWidth;
                int targetRow = y * targetWidth;

                for (int x = 0; x < targetWidth; x++)
                {
                    result[targetRow + x] = source[sourceRow + (int)((long)x * sourceWidth / targetWidth)];
                }
            }

            return result;
        }
    }
}
