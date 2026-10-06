using ClassicUO.Utility.Logging;
using SDL3;
using System;
using System.Runtime.InteropServices;

namespace ClassicUO.Renderer
{
    /// <summary>
    /// Renderer limits FNA does not expose. Values are resolved lazily on first use and cached for the
    /// process lifetime; the OpenGL context's limits do not change across device resets.
    /// </summary>
    public static class GraphicsDeviceCapabilities
    {
        private const int FALLBACK_MAX_TEXTURE_DIMENSION = 8192;
        private const uint GL_MAX_TEXTURE_SIZE = 0x0D33;

        private static readonly Lazy<int> _maxTextureDimension = new Lazy<int>(QueryMaxTextureDimension);

        /// <summary>
        /// The largest width or height a texture may have on the current renderer, in pixels.
        /// Falls back to <see cref="FALLBACK_MAX_TEXTURE_DIMENSION"/> when the renderer cannot be queried,
        /// e.g. on a non-OpenGL backend.
        /// </summary>
        public static int MaxTextureDimension => _maxTextureDimension.Value;

        private static unsafe int QueryMaxTextureDimension()
        {
            try
            {
                if (SDL.SDL_GL_GetCurrentContext() == IntPtr.Zero)
                    return FALLBACK_MAX_TEXTURE_DIMENSION;

                IntPtr proc = SDL.SDL_GL_GetProcAddress("glGetIntegerv");

                if (proc == IntPtr.Zero)
                    return FALLBACK_MAX_TEXTURE_DIMENSION;

                int value;
                ((delegate* unmanaged[Cdecl]<uint, int*, void>)proc)(GL_MAX_TEXTURE_SIZE, &value);

                if (value <= 0)
                    return FALLBACK_MAX_TEXTURE_DIMENSION;

                Log.Info($"Renderer max texture dimension: {value}");
                return value;
            }
            catch (Exception e)
            {
                Log.Warn($"Could not query the renderer max texture dimension, using {FALLBACK_MAX_TEXTURE_DIMENSION}: {e.Message}");
                return FALLBACK_MAX_TEXTURE_DIMENSION;
            }
        }
    }
}
