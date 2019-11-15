using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http3.QPack;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Testing;
using Moq;
using Xunit;
using Xunit.Abstractions;
using static Microsoft.AspNetCore.Server.Kestrel.Core.Tests.Http2TestBase;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Tests
{
    public class Http3TestBase : TestApplicationErrorLoggerLoggedTest, IDisposable
    {
        internal TestServiceContext _serviceContext;
        internal Http3Connection _connection;
        internal readonly TimeoutControl _timeoutControl;
        internal readonly Mock<IKestrelTrace> _mockKestrelTrace = new Mock<IKestrelTrace>();
        internal readonly Mock<ITimeoutHandler> _mockTimeoutHandler = new Mock<ITimeoutHandler>();
        internal readonly Mock<MockTimeoutControlBase> _mockTimeoutControl;
        internal readonly MemoryPool<byte> _memoryPool = SlabMemoryPoolFactory.Create();
        protected Task _connectionTask;
        protected readonly RequestDelegate _echoApplication;
        private TestMultiplexedConnectionContext _multiplexedContext;

        public Http3TestBase()
        {
            _timeoutControl = new TimeoutControl(_mockTimeoutHandler.Object);
            _mockTimeoutControl = new Mock<MockTimeoutControlBase>(_timeoutControl) { CallBase = true };
            _timeoutControl.Debugger = Mock.Of<IDebugger>();
            _echoApplication = async context =>
            {
                var buffer = new byte[Http3PeerSettings.MinAllowedMaxFrameSize];
                var received = 0;

                while ((received = await context.Request.Body.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer, 0, received);
                }
            };
        }

        public override void Initialize(TestContext context, MethodInfo methodInfo, object[] testMethodArguments, ITestOutputHelper testOutputHelper)
        {
            base.Initialize(context, methodInfo, testMethodArguments, testOutputHelper);

            _serviceContext = new TestServiceContext(LoggerFactory, _mockKestrelTrace.Object)
            {
                Scheduler = PipeScheduler.Inline,
            };
        }

        protected async Task InitializeConnectionAsync(RequestDelegate application)
        {
            if (_connection == null)
            {
                CreateConnection();
            }

            _connectionTask = _connection.ProcessRequestsAsync(new DummyApplication(application));

            await Task.CompletedTask;
        }

        internal async ValueTask<Http3RequestStream> InitializeConnectionAndStreamsAsync(RequestDelegate application)
        {
            await InitializeConnectionAsync(application);
 
            var controlStream1 = await CreateControlStream(0);
            var controlStream2 = await CreateControlStream(2);
            var controlStream3 = await CreateControlStream(3);

            return await CreateRequestStream();
        }

        protected void CreateConnection()
        {
            var limits = _serviceContext.ServerOptions.Limits;

            var features = new FeatureCollection();

            _multiplexedContext = new TestMultiplexedConnectionContext(this);

            var httpConnectionContext = new HttpConnectionContext
            {
                ConnectionContext = _multiplexedContext,
                ConnectionFeatures = features,
                ServiceContext = _serviceContext,
                MemoryPool = _memoryPool,
                Transport = null, // Make sure it's null
                TimeoutControl = _mockTimeoutControl.Object
            };

            _connection = new Http3Connection(httpConnectionContext);
            var httpConnection = new HttpConnection(httpConnectionContext);
            httpConnection.Initialize(_connection);
            _mockTimeoutHandler.Setup(h => h.OnTimeout(It.IsAny<TimeoutReason>()))
                           .Callback<TimeoutReason>(r => httpConnection.OnTimeout(r));
        }

        private static PipeOptions GetInputPipeOptions(ServiceContext serviceContext, MemoryPool<byte> memoryPool, PipeScheduler writerScheduler) => new PipeOptions
        (
          pool: memoryPool,
          readerScheduler: serviceContext.Scheduler,
          writerScheduler: writerScheduler,
          pauseWriterThreshold: serviceContext.ServerOptions.Limits.MaxRequestBufferSize ?? 0,
          resumeWriterThreshold: serviceContext.ServerOptions.Limits.MaxRequestBufferSize ?? 0,
          useSynchronizationContext: false,
          minimumSegmentSize: memoryPool.GetMinimumSegmentSize()
        );

        private static PipeOptions GetOutputPipeOptions(ServiceContext serviceContext, MemoryPool<byte> memoryPool, PipeScheduler readerScheduler) => new PipeOptions
        (
            pool: memoryPool,
            readerScheduler: readerScheduler,
            writerScheduler: serviceContext.Scheduler,
            pauseWriterThreshold: GetOutputResponseBufferSize(serviceContext),
            resumeWriterThreshold: GetOutputResponseBufferSize(serviceContext),
            useSynchronizationContext: false,
            minimumSegmentSize: memoryPool.GetMinimumSegmentSize()
        );

        private static long GetOutputResponseBufferSize(ServiceContext serviceContext)
        {
            var bufferSize = serviceContext.ServerOptions.Limits.MaxResponseBufferSize;
            if (bufferSize == 0)
            {
                // 0 = no buffering so we need to configure the pipe so the writer waits on the reader directly
                return 1;
            }

            // null means that we have no back pressure
            return bufferSize ?? 0;
        }

        internal async ValueTask<Http3ControlStream> CreateControlStream(int id)
        {
            var stream = new Http3ControlStream(this);
            _multiplexedContext.AcceptQueue.Writer.TryWrite(stream.StreamContext);
            await stream.WriteStreamIdAsync(id);
            return stream;
        }

        internal ValueTask<Http3RequestStream> CreateRequestStream()
        {
            var stream = new Http3RequestStream(this, _connection);
            _multiplexedContext.AcceptQueue.Writer.TryWrite(stream.StreamContext);
            return new ValueTask<Http3RequestStream>(stream);
        }

        public ValueTask<StreamContext> StartBidirectionalStreamAsync()
        {
            var stream = new Http3RequestStream(this, _connection);
            // TODO put these somewhere to be read.
            return new ValueTask<StreamContext>(stream.StreamContext);
        }

        internal class Http3StreamBase
        {
            protected DuplexPipe.DuplexPipePair _pair;
            protected Http3TestBase _testBase;
            protected Http3Connection _connection;

            protected Task SendAsync(ReadOnlySpan<byte> span)
            {
                var writableBuffer = _pair.Application.Output;
                writableBuffer.Write(span);
                return FlushAsync(writableBuffer);
            }

            protected static async Task FlushAsync(PipeWriter writableBuffer)
            {
                await writableBuffer.FlushAsync().AsTask().DefaultTimeout();
            }
        }

        internal class Http3RequestStream : Http3StreamBase, IHttpHeadersHandler
        {
            internal StreamContext StreamContext { get; }

            public bool IsUnidirectional => false;

            public long StreamId => 0;

            private readonly byte[] _headerEncodingBuffer = new byte[Http3PeerSettings.MinAllowedMaxFrameSize];
            private QPackEncoder _qpackEncoder = new QPackEncoder();
            private QPackDecoder _qpackDecoder = new QPackDecoder(10000, 10000);
            private long _bytesReceived;
            protected readonly Dictionary<string, string> _decodedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public Http3RequestStream(Http3TestBase testBase, Http3Connection connection)
            {
                _testBase = testBase;
                _connection = connection;
                var inputPipeOptions = GetInputPipeOptions(_testBase._serviceContext, _testBase._memoryPool, PipeScheduler.ThreadPool);
                var outputPipeOptions = GetOutputPipeOptions(_testBase._serviceContext, _testBase._memoryPool, PipeScheduler.ThreadPool);

                _pair = DuplexPipe.CreateConnectionPair(inputPipeOptions, outputPipeOptions);
                    
                StreamContext = new TestStreamContext(Direction.BidirectionalInbound);
                StreamContext.Transport = _pair.Transport;
            }

            public async Task<bool> SendHeadersAsync(IEnumerable<KeyValuePair<string, string>> headers)
            {
                var outputWriter = _pair.Application.Output;
                var frame = new Http3Frame();
                frame.PrepareHeaders();
                var buffer = _headerEncodingBuffer.AsMemory();
                var done = _qpackEncoder.BeginEncode(headers, buffer.Span, out var length);
                frame.Length = length;
                // TODO may want to modify behavior of input frames to mock different client behavior (client can send anything).
                Http3FrameWriter.WriteHeader(frame, outputWriter);
                await SendAsync(buffer.Span.Slice(0, length));
                return done;
            }

            internal async Task SendDataAsync(Memory<byte> data)
            {
                var outputWriter = _pair.Application.Output;
                var frame = new Http3Frame();
                frame.PrepareData();
                frame.Length = data.Length;
                Http3FrameWriter.WriteHeader(frame, outputWriter);
                await SendAsync(data.Span);
            }

            internal async Task<IEnumerable<KeyValuePair<string, string>>> ExpectHeadersAsync()
            {
                var http3WithPayload = await ReceiveFrameAsync();
                _qpackDecoder.Decode(http3WithPayload.PayloadSequence, this);
                return _decodedHeaders;
            }

            internal async Task<Memory<byte>> ExpectDataAsync()
            {
                var http3WithPayload = await ReceiveFrameAsync();
                return http3WithPayload.Payload;
            }

            internal async Task<Http3FrameWithPayload> ReceiveFrameAsync(uint maxFrameSize = Http3PeerSettings.DefaultMaxFrameSize)
            {
                var frame = new Http3FrameWithPayload();

                while (true)
                {
                    var result = await _pair.Application.Input.ReadAsync().AsTask().DefaultTimeout();
                    var buffer = result.Buffer;
                    var consumed = buffer.Start;
                    var examined = buffer.Start;
                    var copyBuffer = buffer;

                    try
                    {
                        Assert.True(buffer.Length > 0);

                        if (Http3FrameReader.TryReadFrame(ref buffer, frame, maxFrameSize, out var framePayload))
                        {
                            consumed = examined = framePayload.End;
                            frame.Payload = framePayload.ToArray();
                            return frame;
                        }
                        else
                        {
                            examined = buffer.End;
                        }

                        if (result.IsCompleted)
                        {
                            throw new IOException("The reader completed without returning a frame.");
                        }
                    }
                    finally
                    {
                        _bytesReceived += copyBuffer.Slice(copyBuffer.Start, consumed).Length;
                        _pair.Application.Input.AdvanceTo(consumed, examined);
                    }
                }
            }

            public void OnHeader(ReadOnlySpan<byte> name, ReadOnlySpan<byte> value)
            {
                _decodedHeaders[name.GetAsciiStringNonNullCharacters()] = value.GetAsciiOrUTF8StringNonNullCharacters();
            }

            public void OnHeadersComplete(bool endHeaders)
            {
            }
        }

        internal class Http3FrameWithPayload : Http3Frame
        {
            public Http3FrameWithPayload() : base()
            {
            }

            // This does not contain extended headers
            public Memory<byte> Payload { get; set; }

            public ReadOnlySequence<byte> PayloadSequence => new ReadOnlySequence<byte>(Payload);
        }


        internal class Http3ControlStream : Http3StreamBase
        {
            internal StreamContext StreamContext { get; }

            public bool IsUnidirectional => true;

            public long StreamId => 0;

            public Http3ControlStream(Http3TestBase testBase)
            {
                _testBase = testBase;
                var inputPipeOptions = GetInputPipeOptions(_testBase._serviceContext, _testBase._memoryPool, PipeScheduler.ThreadPool);
                var outputPipeOptions = GetOutputPipeOptions(_testBase._serviceContext, _testBase._memoryPool, PipeScheduler.ThreadPool);

                _pair = DuplexPipe.CreateConnectionPair(inputPipeOptions, outputPipeOptions);

                StreamContext = new TestStreamContext(Direction.UnidirectionalInbound);

                StreamContext.Transport = _pair.Transport;
            }

            public async Task WriteStreamIdAsync(int id)
            {
                var writableBuffer = _pair.Application.Output;

                void WriteSpan(PipeWriter pw)
                {
                    var buffer = pw.GetSpan(sizeHint: 8);
                    var lengthWritten = VariableLengthIntegerHelper.WriteInteger(buffer, id);
                    pw.Advance(lengthWritten);
                }

                WriteSpan(writableBuffer);

                await FlushAsync(writableBuffer);
            }
        }

        private class TestMultiplexedConnectionContext : MultiplexedConnectionContext
        {
            public readonly Channel<StreamContext> AcceptQueue = Channel.CreateUnbounded<StreamContext>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true
            });

            private readonly Http3TestBase _testBase;

            public TestMultiplexedConnectionContext(Http3TestBase testBase)
            {
                _testBase = testBase;
            }

            public override string ConnectionId { get; set; }

            public override IFeatureCollection Features { get; }

            public override IDictionary<object, object> Items { get; set; }

            public override async ValueTask<StreamContext> AcceptAsync(CancellationToken cancellationToken = default)
            {
                while (await AcceptQueue.Reader.WaitToReadAsync())
                {
                    while (AcceptQueue.Reader.TryRead(out var connection))
                    {
                        return connection;
                    }
                }

                return null;
            }

            public override ValueTask<StreamContext> ConnectAsync(IFeatureCollection features = null, bool unidirectional = false, CancellationToken cancellationToken = default)
            {
                var stream = new Http3ControlStream(_testBase);
                // TODO put these somewhere to be read.
                return new ValueTask<StreamContext>(stream.StreamContext);
            }
        }

        private class TestStreamContext : StreamContext
        {
            public TestStreamContext(Direction direction)
            {
                Direction = direction;
            }

            public override long StreamId { get; set; }

            public override Direction Direction { get; }

            public override string ConnectionId { get; set; }

            public override IFeatureCollection Features { get; }

            public override IDictionary<object, object> Items { get; set; }

            public override IDuplexPipe Transport { get; set; }
        }
    }
}
