/* xrloc 的统一原生绑定：桌面使用 xrloc.dll，Android 使用 libxrloc.so，iOS 使用 libxrloc.a（静态链入可执行文件，故库名为 __Internal）。所有 DllImport 集中于此，业务层不直接调用 ABI。复合参数按结构体指针传递；版本须为一个 12 字节输出结构体。姿态结构 success 为单字节，其后三字节填充与 C 布局一致。
Native binding for the xrloc payload (xrloc.dll on desktop, libxrloc.so on
Android, libxrloc.a linked into the executable on iOS — hence __Internal there).
The payload exports the same Cdecl symbols on all three platforms; the
MixedSpaceXrLocServer enum stays top level because scene serialization
(MixedSpaceSDK.Inspector) references it on every platform.
All XrLoc* DllImport declarations live here — keep this the single binding
surface for the xrloc library; business logic should not P/Invoke it directly.
Signatures mirror xrloc_c.h byte for byte. Two traps are worth keeping in mind
when editing the structs: the C ABI takes pointers to every composite argument,
so a struct must be declared as a struct (splitting XrLocVersion into three
out ints would pass three pointers instead of one); and XrLocPose packs a
one-byte bool between confidence and map_id, so success is declared as byte and
the CLR inserts the same three padding bytes the C compiler does. */

using System;
using System.Runtime.InteropServices;

namespace MixedSpace
{
    /// <summary>
    /// SDK Inspector 中显示的原生 Server 环境选择。
    /// Native "Server" profile selector shown on the MixedSpaceSDK Inspector.
    /// </summary>
    public enum MixedSpaceXrLocServer
    {
        Production = 0,
        Test = 1
    }
}

namespace MixedSpace
{
    /// <summary>
    /// 返回整数状态的 XrLoc 调用所使用的状态码；不适用于返回 void 的日志注册接口。
    /// Status codes for XrLoc calls returning integer status values, excluding void log-registration
    /// calls.
    /// </summary>
    internal enum MixedSpaceXrLocStatus
    {
        Ok = 0,
        InvalidArgument = -1,
        Initialization = -2,
        LoadMap = -3,
        LocalizeFailed = -4,
        MapNotFound = -5,
        MapExists = -6,
        AuthorizationFailed = -7,
        Unknown = -99
    }

    /// <summary>
    /// 原生库支持的输入图像像素布局。
    /// Pixel layouts the library accepts for an input frame.
    /// </summary>
    internal enum MixedSpaceXrLocImageFormat
    {
        Gray = 0,
        Bgr = 1,
        Rgb = 2,
        Bgra = 3,
        Rgba = 4
    }

    /// <summary>
    /// 库版本通过一个 12 字节输出参数读回。
    /// Library version, read back as one 12-byte out parameter.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MixedSpaceXrLocVersion
    {
        public int major;
        public int minor;
        public int patch;
    }

    /// <summary>
    /// 针孔内参加两个径向、两个切向系数，共 64 字节。
    /// Pinhole intrinsics plus two radial and two tangential coefficients (64 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MixedSpaceXrLocCamera
    {
        public double fx;
        public double fy;
        public double cx;
        public double cy;
        public double k1;
        public double k2;
        public double p1;
        public double p2;
    }

    /// <summary>
    /// 32 字节原始图像描述符，stride=0 表示紧密行排列。solver_type 非 1 时读取 rotationAngle，不因先验指针非空而改变来源；solver 0 加角度 0
    /// 会固定图像方向，即便先验准确也一样。
    /// Raw image descriptor (32 bytes). stride 0 means rows are contiguous;
    /// rotationAngle is read whenever solver_type is not 1. The payload gates the
    /// angle source on solver_type alone and never looks at the prior pointer, so a
    /// caller that passes solver 0 with rotationAngle 0 pins the frame to an
    /// arbitrary quarter turn regardless of how good the prior is.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MixedSpaceXrLocImage
    {
        public IntPtr data;
        public int width;
        public int height;
        public int stride;
        public MixedSpaceXrLocImageFormat format;
        public int rotationAngle;
    }

    /// <summary>
    /// 112 字节定位输出，旋转为行优先 3x3 相机到世界矩阵，position 为平移列。success 使用一个字节保持可直接传送布局，不把尾部填充当标志。
    /// Localization output (112 bytes). rotation is a row-major 3x3 camera-to-world
    /// matrix and position is its translation column. success is one byte so the
    /// struct stays blittable and the trailing padding is never read as a flag.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MixedSpaceXrLocPose
    {
        public double r00;
        public double r01;
        public double r02;
        public double r10;
        public double r11;
        public double r12;
        public double r20;
        public double r21;
        public double r22;
        public double px;
        public double py;
        public double pz;
        public int confidence;
        public byte success;
        public int mapId;
    }

    /// <summary>
    /// 一个 12 字节的地图点云样本。
    /// One map point cloud sample (12 bytes).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct MixedSpaceXrLocPoint3D
    {
        public float x;
        public float y;
        public float z;
    }

    /// <summary>
    /// 授权、地图、定位和编码共享的 xrloc 入口；桌面 DLL 与 Android ARM64 库使用相同 Cdecl 符号。此处只声明薄 extern，解释、缓冲区编组及状态映射归
    /// MixedSpaceXrLocCore。
    /// Direct xrloc entries shared by authorization, map storage, localization and
    /// frame encoding. Windows, Android and iOS payloads share Cdecl entry points;
    /// iOS resolves statically linked symbols through __Internal.
    /// Every entry point is a thin extern: interpretation, marshalling of buffers and
    /// status mapping live in MixedSpaceXrLocCore.
    /// </summary>
    internal static class MixedSpaceXrLocNative
    {
        // iOS 把 libxrloc.a 静态链入可执行文件，符号不在独立镜像里，只能按 __Internal 解析。
        // On iOS libxrloc.a is linked into the executable, so its symbols are not in a
        // separate image and must be resolved through __Internal.
        private const string Library =
#if UNITY_IOS && !UNITY_EDITOR
            "__Internal";
#else
            "xrloc";
#endif

        #region Authorization and configuration

        [DllImport(Library, EntryPoint = "XrLocSetInteger", CallingConvention = CallingConvention.Cdecl)]
        private static extern int SetIntegerRaw([MarshalAs(UnmanagedType.LPStr)] string parameter, int value);

        [DllImport(Library, EntryPoint = "XrLocGetInteger", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GetIntegerRaw([MarshalAs(UnmanagedType.LPStr)] string parameter, out int value);

        [DllImport(Library, EntryPoint = "XrLocVerifyToken", CallingConvention = CallingConvention.Cdecl)]
        private static extern int VerifyTokenRaw([MarshalAs(UnmanagedType.LPStr)] string token);

        [DllImport(Library, EntryPoint = "XrLocVerifyKey", CallingConvention = CallingConvention.Cdecl)]
        private static extern int VerifyKeyRaw([MarshalAs(UnmanagedType.LPStr)] string localKey);

        /// <summary>
        /// 验证令牌前选择原生服务器环境。成功返回 0；-1 表示参数名或值不接受，仅允许 0、1。
        /// Selects the native server profile before token verification.
        /// Returns 0 (MixedSpaceXrLocStatus.Ok) on success; -1 means the key spelling
        /// or the value fell outside the accepted set (only 0 and 1 are valid).
        /// </summary>
        internal static int SetServer(int serverId)
        {
            return SetIntegerRaw("Server", serverId);
        }

        /// <summary>
        /// 开发者令牌被所选原生服务器接受时返回 0。
        /// Returns 0 when the developer token is accepted by the native server profile.
        /// </summary>
        internal static int VerifyDeveloperToken(string developerToken)
        {
            return VerifyTokenRaw(developerToken);
        }

        /// <summary>
        /// 离线激活密钥被接受时返回 0，无需网络往返，用于无法连接许可服务器的设备。
        /// Returns 0 when an offline activation key is accepted. This channel does not
        /// need a network round trip. Native-issued token caches are another offline path.
        /// </summary>
        internal static int VerifyLocalKey(string localKey)
        {
            return VerifyKeyRaw(localKey);
        }

        /// <summary>
        /// 已知整数选项包括 NumThreads（-1 使用系统默认）和 ImageCompressionLevel（0..9，默认 4）；Server 由 SetServer 管理。
        /// Known integer knobs: "NumThreads" (-1 = system default) and
        /// "ImageCompressionLevel" (0-9, default 4). "Server" is handled by SetServer.
        /// </summary>
        internal static int SetInteger(string parameter, int value)
        {
            return SetIntegerRaw(parameter, value);
        }

        /// <summary>
        /// 读取一个整数配置选项。
        /// Reads one integer knob.
        /// </summary>
        internal static int GetInteger(string parameter, out int value)
        {
            return GetIntegerRaw(parameter, out value);
        }

        #endregion Authorization and configuration

        #region Diagnostics

        /// <summary>
        /// 安装进程级日志输出，签名为 void (*)(const char*)，与 PosePlugin 相同，可共享托管桥接委托。
        /// Installs the process-wide log sink. The callback signature is
        /// void (*)(const char*), identical to the PosePlugin sink, so the same
        /// managed thunk can serve both libraries.
        /// </summary>
        [DllImport(Library, EntryPoint = "XrLocRegisterLogCallback", CallingConvention = CallingConvention.Cdecl)]
        internal static extern void RegisterLogCallback(IntPtr callback);

        [DllImport(Library, EntryPoint = "XrLocGetVersion", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int GetVersion(out MixedSpaceXrLocVersion version);

        #endregion Diagnostics

        #region Map storage

        /// <summary>
        /// 加载地图容器，直接使用调用方提供的应用地图键；重复 ID 返回 MapExists，不替换已驻留副本。
        /// Loads one map container. The caller owns the identifier, so the
        /// application map key can be reused directly; a repeated id returns
        /// MapExists instead of replacing the resident copy.
        /// </summary>
        [DllImport(Library, EntryPoint = "XrLocLoadMap", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int LoadMap(int mapId, byte[] mapData, UIntPtr mapSize);

        [DllImport(Library, EntryPoint = "XrLocFreeMap", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int FreeMap(int mapId);

        [DllImport(Library, EntryPoint = "XrLocPointsGetCount", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PointsGetCount(int mapId, out int count);

        /// <summary>
        /// 向调用方固定的 MixedSpaceXrLocPoint3D 缓冲区复制最多 maxPoints 个样本。
        /// Copies up to maxPoints samples into a caller-pinned buffer of MixedSpaceXrLocPoint3D.
        /// </summary>
        [DllImport(Library, EntryPoint = "XrLocPointsGet", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int PointsGet(int mapId, IntPtr points, int maxPoints, out int written);

        #endregion Map storage

        #region Localization

        /// <summary>
        /// 搜索显式传入的已加载地图 ID，没有搜索全部驻留地图的哨兵。方向参数必须与 solverType 配套：solver 1 使用 rotationPriorXyzw，其他方向探测使用
        /// image.rotationAngle。不能仅凭先验非空就假定覆盖图像角度，具体组合由 MixedSpaceXrLocCore 选择。
        /// Searches the explicitly loaded map IDs; there is no all-resident-maps sentinel. Orientation
        /// must match solverType: solver 1 uses rotationPriorXyzw, while other probes use image.rotationAngle. A non-null
        /// prior alone does not override the angle; MixedSpaceXrLocCore selects the combination.
        /// </summary>
        [DllImport(Library, EntryPoint = "XrLocLocalize", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int Localize(
            int[] mapIds,
            int numMaps,
            ref MixedSpaceXrLocImage image,
            ref MixedSpaceXrLocCamera camera,
            int solverType,
            float[] rotationPriorXyzw,
            out MixedSpaceXrLocPose pose);

        #endregion Localization

        #region Lens correction

        /// <summary>
        /// Rectifies tightly packed pixels. Updated native plugins write the corrected-image
        /// [fx,fy,cx,cy] back into the intrinsics buffer; callers must use those returned values.
        /// </summary>
        [DllImport(Library, EntryPoint = "XrLocUndistortFishEye", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int UndistortFishEye(
            byte[] result,
            int resultSizeMax,
            byte[] pixels,
            int width,
            int height,
            int channels,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 4)] float[] intrinsics,
            float[] distortionCoefficients,
            float alpha,
            out int resultSize);

        #endregion Lens correction

        #region Frame encoding

        /// <summary>
        /// 将原始像素编码为独立 PNG，不提供匹配预处理标志。
        /// Encodes raw pixels into PNG. There is no matching-prep flag: the output is always a standalone
        /// PNG.
        /// </summary>
        [DllImport(Library, EntryPoint = "XrLocCaptureImage", CallingConvention = CallingConvention.Cdecl)]
        internal static extern int CaptureImage(
            IntPtr capture,
            int captureSizeMax,
            IntPtr pixels,
            int width,
            int height,
            int channels,
            out int captureSize);

        #endregion Frame encoding
    }
}
