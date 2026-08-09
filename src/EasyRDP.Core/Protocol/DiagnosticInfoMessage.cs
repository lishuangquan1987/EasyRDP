using System;
using System.IO;
using System.Text;

namespace EasyRDP.Core.Protocol
{
    /// <summary>
    /// 诊断信息消息（服务端→客户端）。携带服务端系统信息，供客户端连接详情面板展示
    /// （参考 ToDesk 系统性能分组）。响应客户端的 DiagnosticInfoRequest（空 payload）。
    /// Payload 布局：CpuNameLen(2) CpuName | CpuCores(4) | GpuNameLen(2) GpuName |
    /// TotalMemoryMb(8) | OsVersionLen(2) OsVersion | CaptureMethod(1) |
    /// ScaleFactorX100(2) | ScreenWidth(4) | ScreenHeight(4) | H264(1) Zrle(1) Vp8(1)
    /// 字符串均为 UTF-8 字节 + ushort 长度前缀。
    /// </summary>
    public class DiagnosticInfoMessage
    {
        private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();

        /// <summary>单个字符串字段最大字节数（CPU/GPU/OS 名称足够）。</summary>
        public const int MaxStrLen = 512;

        /// <summary>CPU 型号名称（如 "Intel(R) Core(TM) i7-1360P"）。</summary>
        public string CpuName;
        /// <summary>CPU 逻辑核心数。</summary>
        public int CpuCores;
        /// <summary>GPU 名称（主适配器）。</summary>
        public string GpuName;
        /// <summary>物理内存总量（MB）。</summary>
        public long TotalMemoryMb;
        /// <summary>操作系统版本描述（如 "Microsoft Windows 10 Pro"）。</summary>
        public string OsVersion;
        /// <summary>服务端屏幕采集方式。0=BitBlt 1=DXGI 2=StretchBlt 缩放 3=镜像驱动。</summary>
        public byte CaptureMethod;
        /// <summary>服务端 DPI 缩放因子 × 100（100=100%，150=150%）。</summary>
        public ushort ScaleFactorX100;
        /// <summary>服务端主屏宽度（物理像素）。</summary>
        public int ScreenWidth;
        /// <summary>服务端主屏高度（物理像素）。</summary>
        public int ScreenHeight;
        /// <summary>服务端 H.264 软件编码是否可用（1=可用）。</summary>
        public byte H264Available;
        /// <summary>服务端 ZRLE 编码是否可用（1=可用）。</summary>
        public byte ZrleAvailable;
        /// <summary>服务端 VP8 编码是否可用（1=可用）。</summary>
        public byte Vp8Available;

        /// <summary>序列化为 payload 字节。</summary>
        public byte[] Pack()
        {
            byte[] cpu = EncodeStr(CpuName);
            byte[] gpu = EncodeStr(GpuName);
            byte[] os = EncodeStr(OsVersion);

            using (var ms = new MemoryStream(64 + cpu.Length + gpu.Length + os.Length))
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write((ushort)cpu.Length);
                bw.Write(cpu);
                bw.Write(CpuCores);
                bw.Write((ushort)gpu.Length);
                bw.Write(gpu);
                bw.Write(TotalMemoryMb);
                bw.Write((ushort)os.Length);
                bw.Write(os);
                bw.Write(CaptureMethod);
                bw.Write(ScaleFactorX100);
                bw.Write(ScreenWidth);
                bw.Write(ScreenHeight);
                bw.Write(H264Available);
                bw.Write(ZrleAvailable);
                bw.Write(Vp8Available);
                return ms.ToArray();
            }
        }

        /// <summary>从 payload 字节反序列化。</summary>
        public static DiagnosticInfoMessage Unpack(byte[] payload)
        {
            // 最小长度 = 3×(2 长度前缀) + CpuCores(4) + TotalMemoryMb(8) + CaptureMethod(1)
            //   + ScaleFactorX100(2) + ScreenWidth(4) + ScreenHeight(4) + 3×可用性(1) = 32
            if (payload == null || payload.Length < 32)
            {
                Logger.Warn("DiagnosticInfo unpack failed: payload too short ({0})",
                    payload != null ? payload.Length : 0);
                throw new ArgumentException("DiagnosticInfo payload too short");
            }
            var msg = new DiagnosticInfoMessage();
            using (var ms = new MemoryStream(payload))
            using (var br = new BinaryReader(ms))
            {
                msg.CpuName = ReadStr(br);
                msg.CpuCores = br.ReadInt32();
                msg.GpuName = ReadStr(br);
                msg.TotalMemoryMb = br.ReadInt64();
                msg.OsVersion = ReadStr(br);
                msg.CaptureMethod = br.ReadByte();
                msg.ScaleFactorX100 = br.ReadUInt16();
                msg.ScreenWidth = br.ReadInt32();
                msg.ScreenHeight = br.ReadInt32();
                msg.H264Available = br.ReadByte();
                msg.ZrleAvailable = br.ReadByte();
                msg.Vp8Available = br.ReadByte();
                return msg;
            }
        }

        private static byte[] EncodeStr(string s)
        {
            return s != null ? Encoding.UTF8.GetBytes(s) : new byte[0];
        }

        private static string ReadStr(BinaryReader br)
        {
            int len = br.ReadUInt16();
            if (len > MaxStrLen)
            {
                Logger.Warn("DiagnosticInfo unpack failed: string len {0} exceeds max {1}", len, MaxStrLen);
                throw new ArgumentException("DiagnosticInfo string too long: " + len);
            }
            byte[] buf = br.ReadBytes(len);
            if (buf.Length != len)
            {
                Logger.Warn("DiagnosticInfo unpack failed: string truncated (expected {0} got {1})", len, buf.Length);
                throw new ArgumentException("DiagnosticInfo string truncated");
            }
            return Encoding.UTF8.GetString(buf);
        }
    }
}
