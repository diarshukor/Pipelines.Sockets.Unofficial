﻿using System.IO.Pipelines;
using Xunit;

namespace Pipelines.Sockets.Unofficial.Tests
{
    public class PipeTests
    {
        [Fact]
        public void PipeLengthWorks()
        {
            var pipe = new Pipe();
            var span = pipe.Writer.GetSpan(42);
            for (int i = 0; i < 42; i++)
                span[i] = (byte)i;
            pipe.Writer.Advance(42);
            pipe.Writer.FlushAsync().AsTask().Wait(); // this is what changes the length

            Assert.Equal(42, SocketConnection.Counters.GetPipeLength(pipe));

            Assert.True(pipe.Reader.TryRead(out var result));
            Assert.Equal(42, result.Buffer.Length);
            Assert.Equal(42, SocketConnection.Counters.GetPipeLength(pipe));

            pipe.Reader.AdvanceTo(result.Buffer.End);
            Assert.Equal(0, SocketConnection.Counters.GetPipeLength(pipe));
        }
    }
}
