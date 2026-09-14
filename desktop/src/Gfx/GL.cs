using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace KerbinMaps.Gfx
{
    public sealed class GlException : Exception
    {
        public GlException(string msg) : base(msg) { }
    }

    /* Enlaces mínimos a OpenGL 3.3 core, sin paquetes externos.

       Las funciones de GL 1.1 salen de opengl32.dll; las modernas (shaders, VAO...)
       solo existen en el controlador y hay que pedírselas con wglGetProcAddress una
       vez hay un contexto activo. Se guardan como punteros a función. */
    public static unsafe class GL
    {
        public const uint COLOR_BUFFER_BIT = 0x4000, DEPTH_BUFFER_BIT = 0x0100;
        public const uint POINTS = 0, LINES = 1, LINE_STRIP = 3, TRIANGLES = 4, TRIANGLE_STRIP = 5;
        public const uint BLEND = 0x0BE2, DEPTH_TEST = 0x0B71, CULL_FACE = 0x0B44, SCISSOR_TEST = 0x0C11,
                          MULTISAMPLE = 0x809D, PROGRAM_POINT_SIZE = 0x8642;
        public const uint ZERO = 0, ONE = 1, SRC_ALPHA = 0x0302, ONE_MINUS_SRC_ALPHA = 0x0303;
        public const uint FRONT = 0x0404, BACK = 0x0405;
        public const uint TEXTURE_2D = 0x0DE1, TEXTURE0 = 0x84C0;
        public const uint RGBA = 0x1908, RGBA8 = 0x8058, UNSIGNED_BYTE = 0x1401, UNSIGNED_INT = 0x1405, FLOAT = 0x1406;
        public const uint TEXTURE_MAG_FILTER = 0x2800, TEXTURE_MIN_FILTER = 0x2801, TEXTURE_WRAP_S = 0x2802, TEXTURE_WRAP_T = 0x2803;
        public const int NEAREST = 0x2600, LINEAR = 0x2601, LINEAR_MIPMAP_LINEAR = 0x2703, REPEAT = 0x2901, CLAMP_TO_EDGE = 0x812F;
        public const uint UNPACK_ALIGNMENT = 0x0CF5, PACK_ALIGNMENT = 0x0D05, TEXTURE_MAX_ANISOTROPY = 0x84FE, MAX_TEXTURE_MAX_ANISOTROPY = 0x84FF;
        public const uint VERTEX_SHADER = 0x8B31, FRAGMENT_SHADER = 0x8B30, COMPILE_STATUS = 0x8B81, LINK_STATUS = 0x8B82, INFO_LOG_LENGTH = 0x8B84;
        public const uint ARRAY_BUFFER = 0x8892, ELEMENT_ARRAY_BUFFER = 0x8893, STATIC_DRAW = 0x88E4, DYNAMIC_DRAW = 0x88E8, STREAM_DRAW = 0x88E0;
        public const uint VENDOR = 0x1F00, RENDERER = 0x1F01, VERSION = 0x1F02, EXTENSIONS = 0x1F03, NUM_EXTENSIONS = 0x821D;
        public const uint MAX_TEXTURE_SIZE = 0x0D33, SAMPLES = 0x80A9;
        public const uint TEXTURE_MAX_LEVEL = 0x813D, LEQUAL = 0x0203;

        [DllImport("opengl32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)]
        static extern IntPtr wglGetProcAddress(string name);

        static IntPtr lib;

        static void* Proc(string name, bool optional = false)
        {
            IntPtr p = wglGetProcAddress(name);
            long v = (long)p;
            if (v >= -1 && v <= 3)
            {
                if (lib == IntPtr.Zero) lib = NativeLibrary.Load("opengl32.dll");
                if (!NativeLibrary.TryGetExport(lib, name, out p)) p = IntPtr.Zero;
            }
            if (p == IntPtr.Zero && !optional) throw new GlException("El controlador no ofrece la función OpenGL " + name);
            return (void*)p;
        }

        static delegate* unmanaged<float, float, float, float, void> _clearColor;
        static delegate* unmanaged<uint, void> _clear;
        static delegate* unmanaged<int, int, int, int, void> _viewport;
        static delegate* unmanaged<int, int, int, int, void> _scissor;
        static delegate* unmanaged<uint, void> _enable;
        static delegate* unmanaged<uint, void> _disable;
        static delegate* unmanaged<uint, uint, void> _blendFunc;
        static delegate* unmanaged<byte, void> _depthMask;
        static delegate* unmanaged<uint, void> _cullFace;
        static delegate* unmanaged<int, uint*, void> _genTextures;
        static delegate* unmanaged<int, uint*, void> _deleteTextures;
        static delegate* unmanaged<uint, uint, void> _bindTexture;
        static delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void> _texImage2D;
        static delegate* unmanaged<uint, int, uint, int, int, int, int, void*, void> _compressedTexImage2D;
        static delegate* unmanaged<uint, uint, int, void> _texParameteri;
        static delegate* unmanaged<uint, uint, float, void> _texParameterf;
        static delegate* unmanaged<uint, int, void> _pixelStorei;
        static delegate* unmanaged<uint, void> _generateMipmap;
        static delegate* unmanaged<uint, void> _activeTexture;
        static delegate* unmanaged<uint, uint> _createShader;
        static delegate* unmanaged<uint, int, byte**, int*, void> _shaderSource;
        static delegate* unmanaged<uint, void> _compileShader;
        static delegate* unmanaged<uint, uint, int*, void> _getShaderiv;
        static delegate* unmanaged<uint, int, int*, byte*, void> _getShaderInfoLog;
        static delegate* unmanaged<uint> _createProgram;
        static delegate* unmanaged<uint, uint, void> _attachShader;
        static delegate* unmanaged<uint, uint, byte*, void> _bindAttribLocation;
        static delegate* unmanaged<uint, void> _linkProgram;
        static delegate* unmanaged<uint, uint, int*, void> _getProgramiv;
        static delegate* unmanaged<uint, int, int*, byte*, void> _getProgramInfoLog;
        static delegate* unmanaged<uint, void> _useProgram;
        static delegate* unmanaged<uint, byte*, int> _getUniformLocation;
        static delegate* unmanaged<int, int, void> _uniform1i;
        static delegate* unmanaged<int, float, void> _uniform1f;
        static delegate* unmanaged<int, float, float, void> _uniform2f;
        static delegate* unmanaged<int, float, float, float, void> _uniform3f;
        static delegate* unmanaged<int, float, float, float, float, void> _uniform4f;
        static delegate* unmanaged<int, int, byte, float*, void> _uniformMatrix4fv;
        static delegate* unmanaged<uint, void> _deleteShader;
        static delegate* unmanaged<uint, void> _deleteProgram;
        static delegate* unmanaged<int, uint*, void> _genVertexArrays;
        static delegate* unmanaged<uint, void> _bindVertexArray;
        static delegate* unmanaged<int, uint*, void> _deleteVertexArrays;
        static delegate* unmanaged<int, uint*, void> _genBuffers;
        static delegate* unmanaged<uint, uint, void> _bindBuffer;
        static delegate* unmanaged<uint, nint, void*, uint, void> _bufferData;
        static delegate* unmanaged<int, uint*, void> _deleteBuffers;
        static delegate* unmanaged<uint, void> _enableVertexAttribArray;
        static delegate* unmanaged<uint, int, uint, byte, int, void*, void> _vertexAttribPointer;
        static delegate* unmanaged<uint, int, int, void> _drawArrays;
        static delegate* unmanaged<uint, int, uint, void*, void> _drawElements;
        static delegate* unmanaged<uint, byte*> _getString;
        static delegate* unmanaged<uint, uint, byte*> _getStringi;
        static delegate* unmanaged<uint, int*, void> _getIntegerv;
        static delegate* unmanaged<uint, float*, void> _getFloatv;
        static delegate* unmanaged<uint> _getError;
        static delegate* unmanaged<int, int, int, int, uint, uint, void*, void> _readPixels;
        static delegate* unmanaged<void> _finish;
        static delegate* unmanaged<int, int> _swapInterval;

        public static float MaxAnisotropy { get; private set; }
        public static int MaxTextureSize { get; private set; }
        public static bool Loaded { get; private set; }

        public static void Load()
        {
            _clearColor = (delegate* unmanaged<float, float, float, float, void>)Proc("glClearColor");
            _clear = (delegate* unmanaged<uint, void>)Proc("glClear");
            _viewport = (delegate* unmanaged<int, int, int, int, void>)Proc("glViewport");
            _scissor = (delegate* unmanaged<int, int, int, int, void>)Proc("glScissor");
            _enable = (delegate* unmanaged<uint, void>)Proc("glEnable");
            _disable = (delegate* unmanaged<uint, void>)Proc("glDisable");
            _blendFunc = (delegate* unmanaged<uint, uint, void>)Proc("glBlendFunc");
            _depthMask = (delegate* unmanaged<byte, void>)Proc("glDepthMask");
            _cullFace = (delegate* unmanaged<uint, void>)Proc("glCullFace");
            _genTextures = (delegate* unmanaged<int, uint*, void>)Proc("glGenTextures");
            _deleteTextures = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteTextures");
            _bindTexture = (delegate* unmanaged<uint, uint, void>)Proc("glBindTexture");
            _texImage2D = (delegate* unmanaged<uint, int, int, int, int, int, uint, uint, void*, void>)Proc("glTexImage2D");
            _compressedTexImage2D = (delegate* unmanaged<uint, int, uint, int, int, int, int, void*, void>)Proc("glCompressedTexImage2D");
            _texParameteri = (delegate* unmanaged<uint, uint, int, void>)Proc("glTexParameteri");
            _texParameterf = (delegate* unmanaged<uint, uint, float, void>)Proc("glTexParameterf");
            _pixelStorei = (delegate* unmanaged<uint, int, void>)Proc("glPixelStorei");
            _generateMipmap = (delegate* unmanaged<uint, void>)Proc("glGenerateMipmap");
            _activeTexture = (delegate* unmanaged<uint, void>)Proc("glActiveTexture");
            _createShader = (delegate* unmanaged<uint, uint>)Proc("glCreateShader");
            _shaderSource = (delegate* unmanaged<uint, int, byte**, int*, void>)Proc("glShaderSource");
            _compileShader = (delegate* unmanaged<uint, void>)Proc("glCompileShader");
            _getShaderiv = (delegate* unmanaged<uint, uint, int*, void>)Proc("glGetShaderiv");
            _getShaderInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Proc("glGetShaderInfoLog");
            _createProgram = (delegate* unmanaged<uint>)Proc("glCreateProgram");
            _attachShader = (delegate* unmanaged<uint, uint, void>)Proc("glAttachShader");
            _bindAttribLocation = (delegate* unmanaged<uint, uint, byte*, void>)Proc("glBindAttribLocation");
            _linkProgram = (delegate* unmanaged<uint, void>)Proc("glLinkProgram");
            _getProgramiv = (delegate* unmanaged<uint, uint, int*, void>)Proc("glGetProgramiv");
            _getProgramInfoLog = (delegate* unmanaged<uint, int, int*, byte*, void>)Proc("glGetProgramInfoLog");
            _useProgram = (delegate* unmanaged<uint, void>)Proc("glUseProgram");
            _getUniformLocation = (delegate* unmanaged<uint, byte*, int>)Proc("glGetUniformLocation");
            _uniform1i = (delegate* unmanaged<int, int, void>)Proc("glUniform1i");
            _uniform1f = (delegate* unmanaged<int, float, void>)Proc("glUniform1f");
            _uniform2f = (delegate* unmanaged<int, float, float, void>)Proc("glUniform2f");
            _uniform3f = (delegate* unmanaged<int, float, float, float, void>)Proc("glUniform3f");
            _uniform4f = (delegate* unmanaged<int, float, float, float, float, void>)Proc("glUniform4f");
            _uniformMatrix4fv = (delegate* unmanaged<int, int, byte, float*, void>)Proc("glUniformMatrix4fv");
            _deleteShader = (delegate* unmanaged<uint, void>)Proc("glDeleteShader");
            _deleteProgram = (delegate* unmanaged<uint, void>)Proc("glDeleteProgram");
            _genVertexArrays = (delegate* unmanaged<int, uint*, void>)Proc("glGenVertexArrays");
            _bindVertexArray = (delegate* unmanaged<uint, void>)Proc("glBindVertexArray");
            _deleteVertexArrays = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteVertexArrays");
            _genBuffers = (delegate* unmanaged<int, uint*, void>)Proc("glGenBuffers");
            _bindBuffer = (delegate* unmanaged<uint, uint, void>)Proc("glBindBuffer");
            _bufferData = (delegate* unmanaged<uint, nint, void*, uint, void>)Proc("glBufferData");
            _deleteBuffers = (delegate* unmanaged<int, uint*, void>)Proc("glDeleteBuffers");
            _enableVertexAttribArray = (delegate* unmanaged<uint, void>)Proc("glEnableVertexAttribArray");
            _vertexAttribPointer = (delegate* unmanaged<uint, int, uint, byte, int, void*, void>)Proc("glVertexAttribPointer");
            _drawArrays = (delegate* unmanaged<uint, int, int, void>)Proc("glDrawArrays");
            _drawElements = (delegate* unmanaged<uint, int, uint, void*, void>)Proc("glDrawElements");
            _getString = (delegate* unmanaged<uint, byte*>)Proc("glGetString");
            _getStringi = (delegate* unmanaged<uint, uint, byte*>)Proc("glGetStringi");
            _getIntegerv = (delegate* unmanaged<uint, int*, void>)Proc("glGetIntegerv");
            _getFloatv = (delegate* unmanaged<uint, float*, void>)Proc("glGetFloatv");
            _getError = (delegate* unmanaged<uint>)Proc("glGetError");
            _readPixels = (delegate* unmanaged<int, int, int, int, uint, uint, void*, void>)Proc("glReadPixels");
            _finish = (delegate* unmanaged<void>)Proc("glFinish");
            _swapInterval = (delegate* unmanaged<int, int>)Proc("wglSwapIntervalEXT", optional: true);

            MaxTextureSize = GetInteger(MAX_TEXTURE_SIZE);
            var ext = new HashSet<string>();
            int n = GetInteger(NUM_EXTENSIONS);
            for (uint i = 0; i < n; i++) ext.Add(GetStringi(EXTENSIONS, i));
            MaxAnisotropy = ext.Contains("GL_EXT_texture_filter_anisotropic") || ext.Contains("GL_ARB_texture_filter_anisotropic")
                ? GetFloat(MAX_TEXTURE_MAX_ANISOTROPY) : 0;
            Loaded = true;
        }

        public static void ClearColor(float r, float g, float b, float a) => _clearColor(r, g, b, a);
        public static void Clear(uint mask) => _clear(mask);
        public static void Viewport(int x, int y, int w, int h) => _viewport(x, y, w, h);
        public static void Scissor(int x, int y, int w, int h) => _scissor(x, y, w, h);
        public static void Enable(uint cap) => _enable(cap);
        public static void Disable(uint cap) => _disable(cap);
        public static void BlendFunc(uint s, uint d) => _blendFunc(s, d);
        public static void DepthMask(bool on) => _depthMask(on ? (byte)1 : (byte)0);
        public static void CullFace(uint mode) => _cullFace(mode);

        public static uint GenTexture() { uint id; _genTextures(1, &id); return id; }
        public static void DeleteTexture(uint id) { if (id != 0) _deleteTextures(1, &id); }
        public static void BindTexture(uint target, uint id) => _bindTexture(target, id);
        public static void TexImage2D(uint target, int level, int internalFormat, int w, int h, uint format, uint type, byte[] data)
        {
            fixed (byte* p = data) _texImage2D(target, level, internalFormat, w, h, 0, format, type, p);
        }
        public static void CompressedTexImage2D(uint target, int level, uint format, int w, int h, byte[] data)
        {
            fixed (byte* p = data) _compressedTexImage2D(target, level, format, w, h, 0, data.Length, p);
        }
        public static void TexParameter(uint target, uint pname, int value) => _texParameteri(target, pname, value);
        public static void TexParameter(uint target, uint pname, float value) => _texParameterf(target, pname, value);
        public static void PixelStore(uint pname, int value) => _pixelStorei(pname, value);
        public static void GenerateMipmap(uint target) => _generateMipmap(target);
        public static void ActiveTexture(uint unit) => _activeTexture(unit);

        public static uint CreateShader(uint type) => _createShader(type);
        public static void ShaderSource(uint shader, string src)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(src);
            fixed (byte* p = bytes)
            {
                byte* s = p;
                int len = bytes.Length;
                _shaderSource(shader, 1, &s, &len);
            }
        }
        public static void CompileShader(uint shader) => _compileShader(shader);
        public static int GetShader(uint shader, uint pname) { int v; _getShaderiv(shader, pname, &v); return v; }
        public static string GetShaderInfoLog(uint shader)
        {
            int len = GetShader(shader, INFO_LOG_LENGTH);
            if (len <= 1) return "";
            var buf = new byte[len];
            int written;
            fixed (byte* p = buf) _getShaderInfoLog(shader, len, &written, p);
            return Encoding.UTF8.GetString(buf, 0, Math.Max(0, written));
        }
        public static uint CreateProgram() => _createProgram();
        public static void AttachShader(uint program, uint shader) => _attachShader(program, shader);
        public static void BindAttribLocation(uint program, uint index, string name)
        {
            byte[] b = Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = b) _bindAttribLocation(program, index, p);
        }
        public static void LinkProgram(uint program) => _linkProgram(program);
        public static int GetProgram(uint program, uint pname) { int v; _getProgramiv(program, pname, &v); return v; }
        public static string GetProgramInfoLog(uint program)
        {
            int len = GetProgram(program, INFO_LOG_LENGTH);
            if (len <= 1) return "";
            var buf = new byte[len];
            int written;
            fixed (byte* p = buf) _getProgramInfoLog(program, len, &written, p);
            return Encoding.UTF8.GetString(buf, 0, Math.Max(0, written));
        }
        public static void UseProgram(uint program) => _useProgram(program);
        public static int GetUniformLocation(uint program, string name)
        {
            byte[] b = Encoding.ASCII.GetBytes(name + "\0");
            fixed (byte* p = b) return _getUniformLocation(program, p);
        }
        public static void Uniform(int loc, int v) => _uniform1i(loc, v);
        public static void Uniform(int loc, float v) => _uniform1f(loc, v);
        public static void Uniform(int loc, float a, float b) => _uniform2f(loc, a, b);
        public static void Uniform(int loc, float a, float b, float c) => _uniform3f(loc, a, b, c);
        public static void Uniform(int loc, float a, float b, float c, float d) => _uniform4f(loc, a, b, c, d);
        public static void UniformMatrix4(int loc, float[] m) { fixed (float* p = m) _uniformMatrix4fv(loc, 1, 0, p); }
        public static void DeleteShader(uint id) => _deleteShader(id);
        public static void DeleteProgram(uint id) => _deleteProgram(id);

        public static uint GenVertexArray() { uint id; _genVertexArrays(1, &id); return id; }
        public static void BindVertexArray(uint id) => _bindVertexArray(id);
        public static void DeleteVertexArray(uint id) { if (id != 0) _deleteVertexArrays(1, &id); }
        public static uint GenBuffer() { uint id; _genBuffers(1, &id); return id; }
        public static void BindBuffer(uint target, uint id) => _bindBuffer(target, id);
        public static void BufferData<T>(uint target, T[] data, int count, uint usage) where T : unmanaged
        {
            fixed (T* p = data) _bufferData(target, (nint)(count * sizeof(T)), p, usage);
        }
        public static void DeleteBuffer(uint id) { if (id != 0) _deleteBuffers(1, &id); }
        public static void EnableVertexAttribArray(uint index) => _enableVertexAttribArray(index);
        public static void VertexAttribPointer(uint index, int size, uint type, bool normalized, int stride, int offset)
            => _vertexAttribPointer(index, size, type, normalized ? (byte)1 : (byte)0, stride, (void*)offset);
        public static void DrawArrays(uint mode, int first, int count) => _drawArrays(mode, first, count);
        public static void DrawElements(uint mode, int count, uint type, int offset) => _drawElements(mode, count, type, (void*)offset);

        public static string GetString(uint name) => Marshal.PtrToStringAnsi((IntPtr)_getString(name)) ?? "";
        public static string GetStringi(uint name, uint index) => Marshal.PtrToStringAnsi((IntPtr)_getStringi(name, index)) ?? "";
        public static int GetInteger(uint name) { int v; _getIntegerv(name, &v); return v; }
        public static float GetFloat(uint name) { float v; _getFloatv(name, &v); return v; }
        public static uint GetError() => _getError();
        public static void ReadPixels(int x, int y, int w, int h, uint format, uint type, byte[] data)
        {
            fixed (byte* p = data) _readPixels(x, y, w, h, format, type, p);
        }
        public static void Finish() => _finish();
        public static void SwapInterval(int interval) { if (_swapInterval != null) _swapInterval(interval); }
    }
}
