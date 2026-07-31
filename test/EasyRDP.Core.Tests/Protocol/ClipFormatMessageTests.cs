namespace EasyRDP.Core.Tests.Protocol
{
    using System;
    using System.Collections.Generic;
    using EasyRDP.Core.Protocol;
    using Xunit;

    /// <summary>
    /// ClipFormatListMessage / ClipFileContentsReqMessage / ClipFileContentsResMessage
    /// 序列化/反序列化单元测试。验证 RustDesk 风格延迟渲染协议消息的 round-trip。
    /// </summary>
    public class ClipFormatMessageTests
    {
        // ── ClipFormatListMessage ──

        /// <summary>ClipFormatList：单文件 + 中文文件名 + 大文件大小的 round-trip。</summary>
        [Fact]
        public void ClipFormatList_RoundTrip_SingleFile_ChineseName()
        {
            var msg = new ClipFormatListMessage
            {
                TransferId = 42,
                Files = new List<ClipFormatListMessage.FileMeta>
                {
                    new ClipFormatListMessage.FileMeta
                    {
                        FileName = "测试文件.txt",
                        FileSize = 9876543210L
                    }
                }
            };

            byte[] payload = msg.Pack();
            var restored = ClipFormatListMessage.Unpack(payload);

            Assert.Equal(42u, restored.TransferId);
            Assert.Equal(1, restored.Files.Count);
            Assert.Equal("测试文件.txt", restored.Files[0].FileName);
            Assert.Equal(9876543210L, restored.Files[0].FileSize);
        }

        /// <summary>ClipFormatList：多文件 round-trip。</summary>
        [Fact]
        public void ClipFormatList_RoundTrip_MultipleFiles()
        {
            var msg = new ClipFormatListMessage
            {
                TransferId = 100,
                Files = new List<ClipFormatListMessage.FileMeta>
                {
                    new ClipFormatListMessage.FileMeta { FileName = "a.bin", FileSize = 1024 },
                    new ClipFormatListMessage.FileMeta { FileName = "b.bin", FileSize = 0 },
                    new ClipFormatListMessage.FileMeta { FileName = "c.bin", FileSize = 4096 }
                }
            };

            byte[] payload = msg.Pack();
            var restored = ClipFormatListMessage.Unpack(payload);

            Assert.Equal(100u, restored.TransferId);
            Assert.Equal(3, restored.Files.Count);
            Assert.Equal("a.bin", restored.Files[0].FileName);
            Assert.Equal(1024L, restored.Files[0].FileSize);
            Assert.Equal("b.bin", restored.Files[1].FileName);
            Assert.Equal(0L, restored.Files[1].FileSize);
            Assert.Equal("c.bin", restored.Files[2].FileName);
            Assert.Equal(4096L, restored.Files[2].FileSize);
        }

        /// <summary>ClipFormatList：空文件列表 round-trip。</summary>
        [Fact]
        public void ClipFormatList_RoundTrip_EmptyFileList()
        {
            var msg = new ClipFormatListMessage
            {
                TransferId = 0,
                Files = new List<ClipFormatListMessage.FileMeta>()
            };

            byte[] payload = msg.Pack();
            var restored = ClipFormatListMessage.Unpack(payload);

            Assert.Equal(0u, restored.TransferId);
            Assert.Equal(0, restored.Files.Count);
        }

        /// <summary>ClipFormatList：空文件名 round-trip（边界值）。</summary>
        [Fact]
        public void ClipFormatList_RoundTrip_EmptyFileName()
        {
            var msg = new ClipFormatListMessage
            {
                TransferId = 1,
                Files = new List<ClipFormatListMessage.FileMeta>
                {
                    new ClipFormatListMessage.FileMeta { FileName = "", FileSize = 100 }
                }
            };

            byte[] payload = msg.Pack();
            var restored = ClipFormatListMessage.Unpack(payload);

            Assert.Equal(1, restored.Files.Count);
            Assert.Equal("", restored.Files[0].FileName);
            Assert.Equal(100L, restored.Files[0].FileSize);
        }

        /// <summary>ClipFormatList：非法文件数量应抛异常。</summary>
        [Fact]
        public void ClipFormatList_Unpack_InvalidFileCount_Throws()
        {
            // 构造一个 file count = -1 的 payload
            var bad = new byte[8];
            bad[0] = 1; bad[1] = 0; bad[2] = 0; bad[3] = 0; // TransferId = 1
            bad[4] = 0xFF; bad[5] = 0xFF; bad[6] = 0xFF; bad[7] = 0xFF; // count = -1
            Assert.Throws<ArgumentException>(() => ClipFormatListMessage.Unpack(bad));
        }

        // ── ClipFileContentsReqMessage ──

        /// <summary>ClipFileContentsReq：完整字段 round-trip。</summary>
        [Fact]
        public void ClipFileContentsReq_RoundTrip_FullFields()
        {
            var msg = new ClipFileContentsReqMessage
            {
                TransferId = 7,
                StreamId = 42,
                FileIndex = 2,
                Flags = ClipFileContentsReqMessage.FlagRange,
                Position = 131072L,
                RequestedSize = 65536L
            };

            byte[] payload = msg.Pack();
            var restored = ClipFileContentsReqMessage.Unpack(payload);

            Assert.Equal(7u, restored.TransferId);
            Assert.Equal(42u, restored.StreamId);
            Assert.Equal(2, restored.FileIndex);
            Assert.Equal(ClipFileContentsReqMessage.FlagRange, restored.Flags);
            Assert.Equal(131072L, restored.Position);
            Assert.Equal(65536L, restored.RequestedSize);
        }

        /// <summary>ClipFileContentsReq：零值 round-trip（边界值）。</summary>
        [Fact]
        public void ClipFileContentsReq_RoundTrip_ZeroValues()
        {
            var msg = new ClipFileContentsReqMessage
            {
                TransferId = 0,
                StreamId = 0,
                FileIndex = 0,
                Flags = 0,
                Position = 0,
                RequestedSize = 0
            };

            byte[] payload = msg.Pack();
            var restored = ClipFileContentsReqMessage.Unpack(payload);

            Assert.Equal(0u, restored.TransferId);
            Assert.Equal(0u, restored.StreamId);
            Assert.Equal(0, restored.FileIndex);
            Assert.Equal(0u, restored.Flags);
            Assert.Equal(0L, restored.Position);
            Assert.Equal(0L, restored.RequestedSize);
        }

        /// <summary>ClipFileContentsReq：payload 过短应抛异常。</summary>
        [Fact]
        public void ClipFileContentsReq_Unpack_TooShort_Throws()
        {
            Assert.Throws<ArgumentException>(() => ClipFileContentsReqMessage.Unpack(new byte[16]));
        }

        // ── ClipFileContentsResMessage ──

        /// <summary>ClipFileContentsRes：成功状态 + 64KB 数据 round-trip。</summary>
        [Fact]
        public void ClipFileContentsRes_RoundTrip_Ok_64KData()
        {
            byte[] data = new byte[64 * 1024];
            for (int i = 0; i < data.Length; i++)
                data[i] = (byte)(i & 0xFF);

            var msg = new ClipFileContentsResMessage
            {
                TransferId = 5,
                StreamId = 100,
                Status = ClipFileContentsResMessage.StatusOk,
                Data = data
            };

            byte[] payload = msg.Pack();
            var restored = ClipFileContentsResMessage.Unpack(payload);

            Assert.Equal(5u, restored.TransferId);
            Assert.Equal(100u, restored.StreamId);
            Assert.Equal(ClipFileContentsResMessage.StatusOk, restored.Status);
            Assert.Equal(data.Length, restored.DataLen);
            Assert.Equal(data.Length, restored.Data.Length);

            // 校验数据完整性
            for (int i = 0; i < data.Length; i++)
                Assert.Equal(data[i], restored.Data[i]);
        }

        /// <summary>ClipFileContentsRes：错误状态 + 空数据 round-trip。</summary>
        [Fact]
        public void ClipFileContentsRes_RoundTrip_ErrorStatus_EmptyData()
        {
            var msg = new ClipFileContentsResMessage
            {
                TransferId = 9,
                StreamId = 200,
                Status = ClipFileContentsResMessage.StatusError,
                Data = new byte[0]
            };

            byte[] payload = msg.Pack();
            var restored = ClipFileContentsResMessage.Unpack(payload);

            Assert.Equal(9u, restored.TransferId);
            Assert.Equal(200u, restored.StreamId);
            Assert.Equal(ClipFileContentsResMessage.StatusError, restored.Status);
            Assert.Equal(0, restored.DataLen);
            Assert.Equal(0, restored.Data.Length);
        }

        /// <summary>ClipFileContentsRes：最后一块不足 64KB round-trip。</summary>
        [Fact]
        public void ClipFileContentsRes_RoundTrip_PartialChunk()
        {
            byte[] data = new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE };

            var msg = new ClipFileContentsResMessage
            {
                TransferId = 1,
                StreamId = 1,
                Status = ClipFileContentsResMessage.StatusOk,
                Data = data
            };

            byte[] payload = msg.Pack();
            var restored = ClipFileContentsResMessage.Unpack(payload);

            Assert.Equal(5, restored.DataLen);
            Assert.Equal(5, restored.Data.Length);
            Assert.Equal(0xEE, restored.Data[4]);
        }

        /// <summary>ClipFileContentsRes：payload 过短应抛异常。</summary>
        [Fact]
        public void ClipFileContentsRes_Unpack_TooShort_Throws()
        {
            Assert.Throws<ArgumentException>(() => ClipFileContentsResMessage.Unpack(new byte[8]));
        }

        /// <summary>ClipFileContentsRes：Data=null 时 Pack 应写 dataLen=0。</summary>
        [Fact]
        public void ClipFileContentsRes_Pack_NullData_WritesZeroLength()
        {
            var msg = new ClipFileContentsResMessage
            {
                TransferId = 1,
                StreamId = 1,
                Status = ClipFileContentsResMessage.StatusOk,
                Data = null
            };

            byte[] payload = msg.Pack();
            var restored = ClipFileContentsResMessage.Unpack(payload);

            Assert.Equal(0, restored.DataLen);
            Assert.Equal(0, restored.Data.Length);
        }
    }
}
