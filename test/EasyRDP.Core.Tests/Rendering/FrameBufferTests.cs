using System.Threading;
using EasyRDP.Core.Rendering;
using Xunit;

namespace EasyRDP.Core.Tests.Rendering
{
    public class FrameBufferTests
    {
        [Fact]
        public void SingleThread_WriteCommitRead_ShouldWork()
        {
            var fb = new FrameBuffer();
            int size = 100 * 100 * 4;
            byte[] writeSlot = fb.BorrowWriteBuffer(size);
            Assert.NotNull(writeSlot);
            Assert.True(writeSlot.Length >= size);

            // Fill with test data
            for (int i = 0; i < size; i++)
                writeSlot[i] = (byte)(i % 256);

            Assert.True(fb.CommitFrame(100, 100));

            ReadFrameRef frame;
            Assert.True(fb.TryBorrowReadFrame(out frame));
            Assert.NotNull(frame.Pixels);
            Assert.Equal(100, frame.Width);
            Assert.Equal(100, frame.Height);
            Assert.Equal(1L, frame.Sequence);

            // Verify data integrity
            for (int i = 0; i < size; i++)
                Assert.Equal((byte)(i % 256), frame.Pixels[i]);

            fb.ReleaseReadFrame();
        }

        [Fact]
        public void Sequence_IncrementsPerCommit()
        {
            var fb = new FrameBuffer();
            int size = 16;

            fb.BorrowWriteBuffer(size);
            fb.CommitFrame(10, 10);
            ReadFrameRef f1;
            fb.TryBorrowReadFrame(out f1);
            Assert.Equal(1L, f1.Sequence);
            fb.ReleaseReadFrame();

            fb.BorrowWriteBuffer(size);
            fb.CommitFrame(10, 10);
            ReadFrameRef f2;
            fb.TryBorrowReadFrame(out f2);
            Assert.Equal(2L, f2.Sequence);
            fb.ReleaseReadFrame();
        }

        [Fact]
        public void FrameCount_IncrementsPerCommit()
        {
            var fb = new FrameBuffer();
            int size = 16;
            for (int i = 0; i < 5; i++)
            {
                fb.BorrowWriteBuffer(size);
                fb.CommitFrame(10, 10);
                ReadFrameRef f;
                fb.TryBorrowReadFrame(out f);
                fb.ReleaseReadFrame();
            }
            Assert.Equal(5, fb.FrameCount);
        }

        [Fact]
        public void BorrowWrite_SucceedsOnOtherSlot_WhenReaderHolding()
        {
            var fb = new FrameBuffer();
            int size = 16;

            fb.BorrowWriteBuffer(size);
            fb.CommitFrame(10, 10);
            ReadFrameRef f;
            fb.TryBorrowReadFrame(out f);

            // Reader has one slot — writer can still borrow the other slot
            byte[] writeSlot = fb.BorrowWriteBuffer(size);
            Assert.NotNull(writeSlot);

            fb.ReleaseReadFrame();
        }

        [Fact]
        public void CommitFrame_ReturnsFalse_WhenReaderHolding()
        {
            var fb = new FrameBuffer();
            int size = 16;

            fb.BorrowWriteBuffer(size);
            fb.CommitFrame(10, 10);
            ReadFrameRef f;
            fb.TryBorrowReadFrame(out f);

            // Writer can borrow other slot (2-slot design), but can't commit until reader releases
            byte[] writeSlot = fb.BorrowWriteBuffer(size);
            Assert.NotNull(writeSlot);
            bool committed = fb.CommitFrame(10, 10);
            Assert.False(committed);

            fb.ReleaseReadFrame();
        }

        [Fact]
        public void DualSlot_AlternatingWrites_ShouldNotOverlap()
        {
            var fb = new FrameBuffer();
            int size = 16;

            byte[] slot1 = fb.BorrowWriteBuffer(size);
            slot1[0] = 0xAA;
            fb.CommitFrame(4, 4);
            ReadFrameRef f1;
            fb.TryBorrowReadFrame(out f1);
            Assert.Equal(0xAA, f1.Pixels[0]);
            fb.ReleaseReadFrame();

            byte[] slot2 = fb.BorrowWriteBuffer(size);
            slot2[0] = 0xBB;
            fb.CommitFrame(4, 4);
            ReadFrameRef f2;
            fb.TryBorrowReadFrame(out f2);
            Assert.Equal(0xBB, f2.Pixels[0]);
            fb.ReleaseReadFrame();

            // Verify first slot not corrupted
            byte[] slot3 = fb.BorrowWriteBuffer(size);
            slot3[0] = 0xCC;
            fb.CommitFrame(4, 4);
            ReadFrameRef f3;
            fb.TryBorrowReadFrame(out f3);
            Assert.Equal(0xCC, f3.Pixels[0]);
            fb.ReleaseReadFrame();
        }

        [Fact]
        public void Reset_ClearsAllState()
        {
            var fb = new FrameBuffer();
            int size = 16;
            fb.BorrowWriteBuffer(size);
            fb.CommitFrame(10, 10);
            ReadFrameRef f;
            fb.TryBorrowReadFrame(out f);
            fb.ReleaseReadFrame();

            fb.Reset();

            Assert.Equal(0, fb.Width);
            Assert.Equal(0, fb.Height);
            Assert.Equal(0, fb.FrameCount);
            Assert.Equal(0L, fb.Sequence);

            ReadFrameRef f2;
            Assert.False(fb.TryBorrowReadFrame(out f2));
        }

        [Fact]
        public void Concurrent_ProducerConsumer_ShouldNotCrash()
        {
            var fb = new FrameBuffer();
            int size = 1024;
            bool running = true;
            int produced = 0;
            int consumed = 0;

            var producer = new Thread(() =>
            {
                while (running)
                {
                    byte[] slot = fb.BorrowWriteBuffer(size);
                    if (slot != null)
                    {
                        slot[0] = 1;
                        if (fb.CommitFrame(16, 16))
                            System.Threading.Interlocked.Increment(ref produced);
                    }
                    Thread.Sleep(1);
                }
            });

            var consumer = new Thread(() =>
            {
                while (running)
                {
                    ReadFrameRef frame;
                    if (fb.TryBorrowReadFrame(out frame))
                    {
                        if (frame.Pixels != null && frame.Pixels.Length > 0)
                            System.Threading.Interlocked.Increment(ref consumed);
                        fb.ReleaseReadFrame();
                    }
                    Thread.Sleep(1);
                }
            });

            producer.Start();
            consumer.Start();
            Thread.Sleep(200);
            running = false;
            producer.Join(1000);
            consumer.Join(1000);

            Assert.True(produced > 0);
            Assert.True(consumed > 0);
        }
    }
}
