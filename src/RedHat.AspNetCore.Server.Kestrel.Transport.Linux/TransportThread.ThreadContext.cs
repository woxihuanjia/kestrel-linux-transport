using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Tmds.Linux;
using static Tmds.Linux.LibC;

namespace RedHat.AspNetCore.Server.Kestrel.Transport.Linux
{
    sealed partial class TransportThread
    {
        sealed class ThreadContext : IDisposable
        {
            private static readonly IPAddress NotIPSocket = IPAddress.None;
            private const int IoVectorsPerAioSocket = 8;
            private const int ListenBacklog = 128;
            private  const int EventBufferLength = 512;
            private const int EPollBlocked = 1;
            private const int EPollNotBlocked = 0;
            private const byte PipeStopThread = 0;
            private const byte PipeActionsPending = 1;
            private const byte PipeStopSockets = 2;
            private const byte PipeCloseAccept = 3;
            private const int MemoryAlignment = 8;

            private readonly int _epollFd;
            private readonly EPoll _epoll;

            private readonly TransportThread _transportThread;
            private readonly LinuxTransportOptions _transportOptions;
            private readonly ILogger _logger;
            // key is the file descriptor
            private readonly Dictionary<int, TSocket> _sockets;
            private readonly List<TSocket> _acceptSockets;
            private readonly Channel<TSocket> _acceptQueue;


            private PipeEndPair _pipeEnds;
            private int _epollState;

            private readonly object _schedulerGate = new object();
            private List<ScheduledSend> _scheduledSendAdding;
            private List<ScheduledSend> _scheduledSendRunning;

            private readonly IntPtr _aioEventsMemory;
            private readonly IntPtr _aioCbsMemory;
            private readonly IntPtr _aioCbsTableMemory;
            private readonly IntPtr _ioVectorTableMemory;
            private unsafe readonly aio_context_t _aioContext;
            private readonly ReadOnlySequence<byte>[] _aioSendBuffers;
            private readonly MemoryHandle[] MemoryHandles;
            public readonly MemoryPool<byte> MemoryPool;

            private unsafe io_event* AioEvents => (io_event*)Align(_aioEventsMemory);
            private unsafe iocb* AioCbs => (iocb*)Align(_aioCbsMemory);
            private unsafe iocb** AioCbsTable => (iocb**)Align(_aioCbsTableMemory);
            private unsafe iovec* IoVectorTable => (iovec*)Align(_ioVectorTableMemory);


            public unsafe ThreadContext(TransportThread transportThread)
            {
                _transportThread = transportThread;
                _sockets = new Dictionary<int, TSocket>();
                _logger = _transportThread.LoggerFactory.CreateLogger($"{nameof(RedHat)}.{nameof(TransportThread)}.{_transportThread.ThreadId}");
                _acceptSockets = new List<TSocket>();
                _transportOptions = transportThread.TransportOptions;
                _scheduledSendAdding = new List<ScheduledSend>(1024);
                _scheduledSendRunning = new List<ScheduledSend>(1024);
                _epollState = EPollBlocked;
                if (_transportOptions.AioReceive | _transportOptions.AioSend)
                {
                    _aioEventsMemory = AllocMemory(sizeof(io_event) * EventBufferLength);
                    _aioCbsMemory = AllocMemory(sizeof(iocb) * EventBufferLength);
                    _aioCbsTableMemory = AllocMemory(IntPtr.Size * EventBufferLength);
                    _ioVectorTableMemory = AllocMemory(SizeOf.iovec * IoVectorsPerAioSocket * EventBufferLength);
                    for (int i = 0; i < EventBufferLength; i++)
                    {
                        AioCbsTable[i] = &AioCbs[i];
                    }
                    if (_transportOptions.AioSend)
                    {
                        _aioSendBuffers = new ReadOnlySequence<byte>[EventBufferLength];
                    }
                }
                int maxMemoryHandleCount = TSocket.MaxIOVectorReceiveLength;
                if (_transportOptions.AioReceive || _transportOptions.AioSend)
                {
                    maxMemoryHandleCount = Math.Max(maxMemoryHandleCount, EventBufferLength);
                }
                if (_transportOptions.DeferSend)
                {
                    maxMemoryHandleCount = Math.Max(maxMemoryHandleCount, TSocket.MaxIOVectorSendLength);
                }
                MemoryHandles = new MemoryHandle[maxMemoryHandleCount];

                // These members need to be Disposed
                _epoll = EPoll.Create();
                _epollFd = _epoll.DangerousGetHandle().ToInt32();
                MemoryPool = CreateMemoryPool();
                _pipeEnds = PipeEnd.CreatePair(blocking: false);
                if (_aioEventsMemory != IntPtr.Zero)
                {
                    aio_context_t ctx;
                    AioInterop.IoSetup(EventBufferLength, &ctx).ThrowOnError();
                    _aioContext = ctx;
                }

                // Single reader, single writer queue since all writes happen from the TransportThread and reads happen sequentially
                // This channel is unbounded which means there's nothing limiting the number of sockets we're accepting.
                // This is similar to having an unbounded number of thread pool work items queued to invoke a ConnectionHandler
                // which was the previous pattern, but now it's more explicit.

                // TODO: Find a reasonable limit and start applying accept backpressure once the channel reaches that limit.
                _acceptQueue = Channel.CreateUnbounded<TSocket>(new UnboundedChannelOptions
                {
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = _transportOptions.ApplicationSchedulingMode == PipeScheduler.Inline,
                });
            }

            public async ValueTask<TSocket> AcceptAsync(CancellationToken cancellationToken = default)
            {
                while (await _acceptQueue.Reader.WaitToReadAsync())
                {
                    while (_acceptQueue.Reader.TryRead(out var connection))
                    {
                        return connection;
                    }
                }

                return null;
            }

            private async Task AbortQueuedConnectionAsync()
            {
                while (await _acceptQueue.Reader.WaitToReadAsync())
                {
                    while (_acceptQueue.Reader.TryRead(out var connection))
                    {
                        connection.Abort();
                    }
                }
            }

            private TSocket CreateAcceptSocket(IPEndPoint endPoint, SocketFlags flags)
            {
                int acceptSocketFd = -1;
                int port = endPoint.Port;
                try
                {
                    bool ipv4 = endPoint.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork;
                    SocketInterop.Socket(ipv4 ? AF_INET : AF_INET6, SOCK_STREAM, IPPROTO_TCP, blocking: false,
                        out acceptSocketFd).ThrowOnError();

                    TSocket acceptSocket = new TSocket(this, acceptSocketFd, flags, _transportOptions);

                    if (!ipv4)
                    {
                        // Kestrel does mapped ipv4 by default.
                        acceptSocket.SetSocketOption(SOL_IPV6, IPV6_V6ONLY, 0);
                    }
                    if (_transportOptions.ReceiveOnIncomingCpu)
                    {
                        if (_transportThread.CpuId != -1)
                        {
                            if (!acceptSocket.TrySetSocketOption(SOL_SOCKET, SO_INCOMING_CPU, _transportThread.CpuId))
                            {
                                _logger.LogWarning($"Cannot enable SO_INCOMING_CPU for {endPoint}");
                            }
                        }
                    }
                    // Linux: allow bind during linger time
                    acceptSocket.SetSocketOption(SOL_SOCKET, SO_REUSEADDR, 1);
                    // Linux: allow concurrent binds and let the kernel do load-balancing
                    acceptSocket.SetSocketOption(SOL_SOCKET, SO_REUSEPORT, 1);
                    if ((flags & SocketFlags.DeferAccept) != 0)
                    {
                        // Linux: wait up to 1 sec for data to arrive before accepting socket
                        acceptSocket.SetSocketOption(SOL_TCP, TCP_DEFER_ACCEPT, 1);
                    }
                    acceptSocket.ZeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                    if (_transportOptions.ZeroCopy && _transportOptions.ZeroCopyThreshold != LinuxTransportOptions.NoZeroCopy)
                    {
                        if (acceptSocket.TrySetSocketOption(SOL_SOCKET, SO_ZEROCOPY, 1))
                        {
                            acceptSocket.ZeroCopyThreshold = _transportOptions.ZeroCopyThreshold;
                        }
                    }

                    var bindResult = acceptSocket.TryBind(endPoint);

                    if (bindResult == PosixResult.EADDRINUSE)
                    {
                        throw new AddressInUseException("Address in use.");
                    }
                    
                    if (bindResult == PosixResult.EADDRNOTAVAIL)
                    {
                        throw new AddressNotAvailableException("Address not available.");
                    }
                    
                    bindResult.ThrowOnError();
                    
                    if (port == 0)
                    {
                        // When testing we want the OS to select a free port
                        port = acceptSocket.GetLocalIPAddress().Port;
                    }

                    acceptSocket.Listen(ListenBacklog);

                    endPoint.Port = port;
                    return acceptSocket;
                }
                catch
                {
                    if (acceptSocketFd != -1)
                    {
                        IOInterop.Close(acceptSocketFd);
                    }
                    throw;
                }
            }

            private int PipeKey => _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32();

            private void Start()
            {
                // register pipe
                EPollInterop.EPollControl(_epollFd,
                                        EPOLL_CTL_ADD,
                                        _pipeEnds.ReadEnd.DangerousGetHandle().ToInt32(),
                                        EPOLLIN,
                                        PipeKey);

                // create accept socket
                {
                    TSocket acceptSocket;
                    SocketFlags flags = SocketFlags.None;
                    if (_transportOptions.DeferSend)
                    {
                        flags |= SocketFlags.DeferSend;
                    };
                    if (_transportThread.AcceptThread != null)
                    {
                        flags |= SocketFlags.TypePassFd;
                        int acceptSocketFd = _transportThread.AcceptThread.CreateReceiveSocket();
                        acceptSocket = new TSocket(this, acceptSocketFd, flags, _transportOptions);
                        acceptSocket.ZeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                    }
                    else
                    {
                        flags |= SocketFlags.TypeAccept;
                        acceptSocket = CreateAcceptSocket(_transportThread.EndPoint, flags);
                    }
                    // accept connections
                    AcceptOn(acceptSocket);
                }
            }

            public unsafe void Run()
            {
                try
                {
                    Start();
                    CompleteStateChange(TransportThreadState.Started);
                }
                catch (Exception e)
                {
                    CompleteStateChange(TransportThreadState.Stopped, e);
                    return;
                }

                try
                {
                    var buffer = stackalloc epoll_event[EventBufferLength];
                    int statReadEvents = 0;
                    int statWriteEvents = 0;
                    int statAcceptEvents = 0;
                    int statAccepts = 0;
                    int statZeroCopySuccess = 0;
                    int statZeroCopyCopied = 0;

                    var acceptableSockets = new List<TSocket>(1);
                    var readableSockets = new List<TSocket>(EventBufferLength);
                    var writableSockets = new List<TSocket>(EventBufferLength);
                    var reregisterEventSockets = new List<TSocket>(EventBufferLength);
                    var zeroCopyCompletions = new List<TSocket>(EventBufferLength);
                    bool pipeReadable = false;

                    bool running = true;
                    do
                    {
                        int numEvents = EPollInterop.EPollWait(_epollFd, buffer, EventBufferLength, timeout: EPoll.TimeoutInfinite).IntValue;

                        // actions can be scheduled without unblocking epoll
                        SetEpollNotBlocked();

                        // check events
                        // we don't handle them immediately:
                        // - this ensures we don't mismatch a closed socket with a new socket that have the same fd
                        //     ~ To have the same fd, the previous fd must be closed, which means it is removed from the epoll
                        //     ~ and won't show up in our next call to epoll.Wait.
                        //     ~ The old fd may be present in the buffer still, but lookup won't give a match, since it is removed
                        //     ~ from the dictionary before it is closed. If we were accepting already, a new socket could match.
                        // - this also improves cache/cpu locality of the lookup
                        epoll_event* ev = buffer;
                        lock (_sockets)
                        {
                            for (int i = 0; i < numEvents; i++)
                            {
                                //   Packed             Non-Packed
                                //   ------             ------
                                // 0:Events       ==    Events
                                // 1:Int1 = Key         [Padding]
                                // 2:Int2 = Key   ==    Int1 = Key
                                // 3:~~~~~~~~~~         Int2 = Key
                                //                      ~~~~~~~~~~
                                int events = (int)ev->events;
                                int key = ev->data.fd;
                                ev++;
                                TSocket tsocket;
                                if (_sockets.TryGetValue(key, out tsocket))
                                {
                                    var type = tsocket.Type;
                                    if (type == SocketFlags.TypeClient)
                                    {
                                        lock (tsocket.Gate)
                                        {
                                            var pendingEventState = tsocket.PendingEventState;

                                            // zero copy
                                            if ((pendingEventState & EPOLLERR & events) != 0)
                                            {
                                                var copyResult = SocketInterop.CompleteZeroCopy(tsocket.Fd);
                                                if (copyResult != PosixResult.EAGAIN)
                                                {
                                                    events &= ~EPOLLERR;
                                                    pendingEventState &= ~EPOLLERR;
                                                    zeroCopyCompletions.Add(tsocket);
                                                    if (copyResult == SocketInterop.ZeroCopyCopied)
                                                    {
                                                        tsocket.ZeroCopyThreshold = LinuxTransportOptions.NoZeroCopy;
                                                        statZeroCopyCopied++;
                                                    }
                                                    else if (copyResult == SocketInterop.ZeroCopySuccess)
                                                    {
                                                        statZeroCopySuccess++;
                                                    }
                                                    else
                                                    {
                                                        Environment.FailFast($"Error occurred while trying to complete zero copy: {copyResult}");
                                                    }
                                                }
                                            }

                                            // treat Error as Readable, Writable
                                            if ((events & EPOLLERR) != 0)
                                            {
                                                events |= EPOLLIN | EPOLLOUT;
                                            }

                                            events &= pendingEventState & (EPOLLIN | EPOLLOUT);
                                            // readable
                                            if ((events & EPOLLIN) != 0)
                                            {
                                                readableSockets.Add(tsocket);
                                                pendingEventState &= ~EPOLLIN;
                                            }
                                            // writable
                                            if ((events & EPOLLOUT) != 0)
                                            {
                                                writableSockets.Add(tsocket);
                                                pendingEventState &= ~EPOLLOUT;
                                            }

                                            // reregister
                                            tsocket.PendingEventState = pendingEventState;
                                            if ((pendingEventState & (EPOLLIN | EPOLLOUT)) != 0)
                                            {
                                                tsocket.PendingEventState |= TSocket.EventControlPending;
                                                reregisterEventSockets.Add(tsocket);
                                            }
                                        }
                                    }
                                    else
                                    {
                                        statAcceptEvents++;
                                        acceptableSockets.Add(tsocket);
                                    }
                                }
                                else if (key == PipeKey)
                                {
                                    pipeReadable = true;
                                }
                            }
                        }

                        // zero copy
                        for (int i = 0; i < zeroCopyCompletions.Count; i++)
                        {
                            zeroCopyCompletions[i].OnZeroCopyCompleted();
                        }
                        zeroCopyCompletions.Clear();

                        // handle accepts
                        statAcceptEvents += acceptableSockets.Count;
                        for (int i = 0; i < acceptableSockets.Count; i++)
                        {
                            statAccepts += HandleAccept(acceptableSockets[i]);
                        }
                        acceptableSockets.Clear();

                        // handle writes
                        statWriteEvents += writableSockets.Count;
                        for (int i = 0; i < writableSockets.Count; i++)
                        {
                            writableSockets[i].OnWritable(stopped: false);
                        }
                        writableSockets.Clear();

                        // handle reads
                        statReadEvents += readableSockets.Count;
                        if (!_transportOptions.AioReceive)
                        {
                            Span<MemoryHandle> receiveMemoryHandles = MemoryHandles;
                            for (int i = 0; i < readableSockets.Count; i++)
                            {
                                TSocket socket = readableSockets[i];
                                var receiveResult = socket.Receive(receiveMemoryHandles);
                                socket.OnReceiveFromSocket(receiveResult);
                            }
                            readableSockets.Clear();
                        }
                        else if (readableSockets.Count > 0)
                        {
                            AioReceive(readableSockets);
                        }

                        // reregister for events
                        for (int i = 0; i < reregisterEventSockets.Count; i++)
                        {
                            var tsocket = reregisterEventSockets[i];
                            lock (tsocket.Gate)
                            {
                                var pendingEventState = tsocket.PendingEventState & ~TSocket.EventControlPending;
                                tsocket.PendingEventState = pendingEventState;
                                UpdateEPollControl(tsocket, pendingEventState, registered: true);
                            }
                        }
                        reregisterEventSockets.Clear();

                        // handle pipe
                        if (pipeReadable)
                        {
                            PosixResult result;
                            do
                            {
                                result = _pipeEnds.ReadEnd.TryReadByte();
                                if (result.Value == PipeStopSockets)
                                {
                                    StopSockets();
                                }
                                else if (result.Value == PipeStopThread)
                                {
                                    running = false;
                                }
                                else if (result.Value == PipeCloseAccept)
                                {
                                    CloseAccept();
                                }
                            } while (result);
                            pipeReadable = false;
                        }

                        // scheduled work
                        // note: this may write a byte to the pipe
                        DoScheduledWork(_transportOptions.AioSend);
                    } while (running);

                    _logger.LogDebug($"Stats A/AE:{statAccepts}/{statAcceptEvents} RE:{statReadEvents} WE:{statWriteEvents} ZCS/ZCC:{statZeroCopySuccess}/{statZeroCopyCopied}");

                    CompleteStateChange(TransportThreadState.Stopped);
                }
                catch (Exception ex)
                {
                    Console.WriteLine(ex.Message);
                    Console.WriteLine(ex.StackTrace);
                    Environment.FailFast("TransportThread", ex);
                }
            }

            private unsafe void AioReceive(List<TSocket> readableSockets)
            {
                ulong PackReceiveState(int received, int advanced, int iovLength) => ((ulong)received << 32) + (ulong)(advanced << 8) + (ulong)(iovLength);
                (int received, int advanced, int iovLength) UnpackReceiveState(ulong data) => ((int)(data >> 32), (int)((data >> 8) & 0xffffff), (int)(data & 0xff));

                int readableSocketCount = readableSockets.Count;
                iocb* aioCb = AioCbs;
                iovec* ioVectors = IoVectorTable;
                PosixResult* receiveResults = stackalloc PosixResult[readableSocketCount];
                Span<MemoryHandle> receiveMemoryHandles = MemoryHandles;
                int receiveMemoryHandleCount = 0;
                for (int i = 0; i < readableSocketCount; i++)
                {
                    TSocket socket = readableSockets[i];
                    var memoryAllocation = socket.DetermineMemoryAllocationForReceive(IoVectorsPerAioSocket);
                    int advanced = socket.FillReceiveIOVector(memoryAllocation, ioVectors, receiveMemoryHandles);

                    aioCb->aio_fildes = socket.Fd;
                    aioCb->aio_data = PackReceiveState(0, advanced, memoryAllocation.IovLength);
                    aioCb->aio_lio_opcode = IOCB_CMD_PREADV;
                    aioCb->aio_buf = (ulong)ioVectors;
                    aioCb->aio_nbytes = (ulong)memoryAllocation.IovLength;
                    aioCb++;

                    ioVectors += memoryAllocation.IovLength;
                    receiveMemoryHandleCount += memoryAllocation.IovLength;
                    receiveMemoryHandles = receiveMemoryHandles.Slice(memoryAllocation.IovLength);
                }
                int eAgainCount = 0;
                while (readableSocketCount > 0)
                {
                    PosixResult res = AioInterop.IoSubmit(_aioContext, readableSocketCount, AioCbsTable);
                    if (res != readableSocketCount)
                    {
                        throw new NotSupportedException("Unexpected IoSubmit retval " + res);
                    }

                    io_event* aioEvents = AioEvents;
                    res = AioInterop.IoGetEvents(_aioContext, readableSocketCount, aioEvents);
                    if (res != readableSocketCount)
                    {
                        throw new NotSupportedException("Unexpected IoGetEvents retval " + res);
                    }
                    int socketsRemaining = readableSocketCount;
                    bool allEAgain = true;
                    io_event* aioEvent = aioEvents;
                    for (int i = 0; i < readableSocketCount; i++)
                    {
                        PosixResult result = new PosixResult((int)aioEvent->res);
                        int socketIndex = i; // assumes in-order events
                        TSocket socket = readableSockets[socketIndex];
                        (int received, int advanced, int iovLength) = UnpackReceiveState(aioEvent->data);
                        (bool done, PosixResult retval) = socket.InterpretReceiveResult(result, ref received, advanced, (iovec*)((iocb*)aioEvent->obj)->aio_buf, iovLength);
                        if (done)
                        {
                            receiveResults[socketIndex] = retval;
                            socketsRemaining--;
                            ((iocb*)aioEvent->obj)->aio_lio_opcode = IOCB_CMD_NOOP;
                            allEAgain = false;
                        }
                        else if (retval != PosixResult.EAGAIN)
                        {
                            ((iocb*)aioEvent->obj)->aio_data = PackReceiveState(received, advanced, iovLength);
                            allEAgain = false;
                        }
                        aioEvent++;
                    }
                    if (socketsRemaining > 0)
                    {
                        if (allEAgain)
                        {
                            eAgainCount++;
                            if (eAgainCount == TransportConstants.MaxEAgainCount)
                            {
                                throw new NotSupportedException("Too many EAGAIN, unable to receive available bytes.");
                            }
                        }
                        else
                        {
                            aioCb = AioCbs;
                            iocb* aioCbWriteAt = aioCb;
                            // The kernel doesn't handle Noop, we need to remove them from the aioCbs
                            for (int i = 0; i < readableSocketCount; i++)
                            {
                                if (aioCb[i].aio_lio_opcode != IOCB_CMD_NOOP)
                                {
                                    if (aioCbWriteAt != aioCb)
                                    {
                                        *aioCbWriteAt = *aioCb;
                                    }
                                    aioCbWriteAt++;
                                }
                                aioCb++;
                            }
                            readableSocketCount = socketsRemaining;
                            eAgainCount = 0;
                        }
                    }
                    else
                    {
                        readableSocketCount = 0;
                    }
                }
                receiveMemoryHandles = MemoryHandles;
                for (int i = 0; i < receiveMemoryHandleCount; i++)
                {
                    receiveMemoryHandles[i].Dispose();
                }
                for (int i = 0; i < readableSockets.Count; i++)
                {
                    readableSockets[i].OnReceiveFromSocket(receiveResults[i]);
                }
                readableSockets.Clear();
            }

            private void StopSockets()
            {
                Dictionary<int, TSocket> clone;
                lock (_sockets)
                {
                    clone = new Dictionary<int, TSocket>(_sockets);
                }
                foreach (var kv in clone)
                {
                    var tsocket = kv.Value;
                    tsocket.Abort();
                }
            }

            private int HandleAccept(TSocket tacceptSocket)
            {
                var type = tacceptSocket.Type;
                int clientFd = -1;
                PosixResult result;
                if (type == SocketFlags.TypeAccept)
                {
                    // TODO: should we handle more than 1 accept? If we do, we shouldn't be to eager
                    //       as that might give the kernel the impression we have nothing to do
                    //       which could interfere with the SO_REUSEPORT load-balancing.
                    result = tacceptSocket.TryAccept(out clientFd, blocking: false);
                }
                else
                {
                    result = tacceptSocket.TryReceiveSocket(out clientFd, blocking: false);
                    if (result.Value == 0)
                    {
                        // The socket passing us file descriptors has closed.
                        // We dispose our end so we get get removed from the epoll.
                        tacceptSocket.Close();
                        return 0;
                    }
                }
                if (result.IsSuccess)
                {
                    TSocket tsocket;
                    try
                    {
                        SocketFlags flags = SocketFlags.TypeClient | (tacceptSocket.IsDeferSend ? SocketFlags.DeferSend : SocketFlags.None);
                        tsocket = new TSocket(this, clientFd, flags, _transportOptions)
                        {
                            ZeroCopyThreshold = tacceptSocket.ZeroCopyThreshold
                        };

                        var localIpEndPoint = tacceptSocket.LocalEndPoint as IPEndPoint;
                        bool ipSocket = !object.ReferenceEquals(localIpEndPoint?.Address, NotIPSocket);

                        // Store the last LocalAddress on the tacceptSocket so we might reuse it instead
                        // of allocating a new one for the same address.
                        IPEndPointStruct localAddress = default(IPEndPointStruct);
                        IPEndPointStruct remoteAddress = default(IPEndPointStruct);
                        if (ipSocket && tsocket.TryGetLocalIPAddress(out localAddress, localIpEndPoint?.Address))
                        {
                            tsocket.LocalEndPoint = new IPEndPoint(localAddress.Address, localAddress.Port);

                            if (tsocket.TryGetPeerIPAddress(out remoteAddress))
                            {
                                tsocket.RemoteEndPoint = new IPEndPoint(remoteAddress.Address, remoteAddress.Port);
                            }
                        }
                        else
                        {
                            // This is not an IP socket.
                            // REVIEW: Should LocalEndPoint be null instead? Some other EndPoint type?
                            tacceptSocket.LocalEndPoint = new IPEndPoint(NotIPSocket, 0);
                            ipSocket = false;
                        }

                        if (ipSocket)
                        {
                            tsocket.SetSocketOption(SOL_TCP, TCP_NODELAY, 1);
                        }
                    }
                    catch
                    {
                        IOInterop.Close(clientFd);
                        return 0;
                    }

                    bool accepted = _acceptQueue.Writer.TryWrite(tsocket);
                    Debug.Assert(accepted, "The connection was not written to the channel!");

                    lock (_sockets)
                    {
                        _sockets.Add(clientFd, tsocket);
                    }

                    bool dataMayBeAvailable = tacceptSocket.IsDeferAccept;
                    tsocket.Start(dataMayBeAvailable);

                    return 1;
                }
                else
                {
                    return 0;
                }
            }


            private void AcceptOn(TSocket tsocket)
            {
                try
                {
                    _acceptSockets.Add(tsocket);
                    lock (_sockets)
                    {
                        _sockets.Add(tsocket.Fd, tsocket);
                    }

                    EPollInterop.EPollControl(_epollFd,
                                              EPOLL_CTL_ADD,
                                              tsocket.Fd,
                                              EPOLLIN,
                                              tsocket.Fd);
                }
                catch
                {
                    tsocket.Close();
                    _acceptSockets.Remove(tsocket);
                    lock (_sockets)
                    {
                        _sockets.Remove(tsocket.Fd);
                    }
                    throw;
                }
            }

            private unsafe IntPtr AllocMemory(int length)
            {
                IntPtr res = Marshal.AllocHGlobal(length + MemoryAlignment - 1);
                Span<byte> span = new Span<byte>(Align(res), length);
                span.Clear();
                return res;
            }

            private unsafe void* Align(IntPtr p)
            {
                ulong pointer = (ulong)p;
                pointer += MemoryAlignment - 1;
                pointer &= ~(ulong)(MemoryAlignment - 1);
                return (void*)pointer;
            }

            public void SetEpollNotBlocked()
            {
                Volatile.Write(ref _epollState, EPollNotBlocked);
            }

            public void ScheduleSend(TSocket socket)
            {
                int epollState;
                lock (_schedulerGate)
                {
                    epollState = Interlocked.CompareExchange(ref _epollState, EPollNotBlocked, EPollBlocked);
                    _scheduledSendAdding.Add(new ScheduledSend { Socket = socket });
                }
                if (epollState == EPollBlocked)
                {
                    _pipeEnds.WriteEnd.WriteByte(PipeActionsPending);
                }
            }

            public void DoScheduledWork(bool aioSend)
            {
                List<ScheduledSend> sendQueue;
                lock (_schedulerGate)
                {
                    sendQueue = _scheduledSendAdding;
                    _scheduledSendAdding = _scheduledSendRunning;
                    _scheduledSendRunning = sendQueue;
                }
                if (sendQueue.Count > 0)
                {
                    PerformSends(sendQueue, aioSend);
                }

                bool unblockEPoll = false;
                lock (_schedulerGate)
                {
                    if (_scheduledSendAdding.Count > 0)
                    {
                        unblockEPoll = true;
                    }
                    else
                    {
                        Volatile.Write(ref _epollState, EPollBlocked);
                    }
                }
                if (unblockEPoll)
                {
                    _pipeEnds.WriteEnd.WriteByte(PipeActionsPending);
                }
            }

            public void RequestCloseAccept() => TrySendToPipe(PipeCloseAccept);

            public void RequestStopSockets() => TrySendToPipe(PipeStopSockets);

            private void TrySendToPipe(byte operation)
            {
                try
                {
                    _pipeEnds.WriteEnd.WriteByte(operation);
                }
                // All sockets stopped already and the PipeEnd was disposed
                catch (IOException ex) when (ex.HResult == EPIPE)
                { }
                catch (ObjectDisposedException)
                { }
            }

            public unsafe void Dispose()
            {
                AbortQueuedConnectionAsync().GetAwaiter().GetResult();

                _epoll?.Dispose();
                _pipeEnds.Dispose();
                MemoryPool?.Dispose();
                if (_aioEventsMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioEventsMemory);
                }
                if (_aioCbsMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioCbsMemory);
                }
                if (_aioCbsTableMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_aioCbsTableMemory);
                }
                if (_ioVectorTableMemory != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(_ioVectorTableMemory);
                }
                if (_aioContext.ring != null)
                {
                    AioInterop.IoDestroy(_aioContext);
                }
            }

            public bool RemoveSocket(int tsocketKey)
            {
                var sockets = _sockets;
                lock (sockets)
                {
                    sockets.Remove(tsocketKey);
                    return sockets.Count == 0;
                }
            }

            public void StopThread()
            {
                _pipeEnds.WriteEnd.WriteByte(PipeStopThread);
            }

            private void CompleteStateChange(TransportThreadState state, Exception error = null)
            {
                _transportThread.CompleteStateChange(state, error);
            }

            private unsafe void AioSend(List<ScheduledSend> sendQueue)
            {
                while (sendQueue.Count > 0)
                {
                    int sendCount = 0;
                    int completedCount = 0;
                    iocb* aioCbs = AioCbs;
                    iovec* ioVectors = IoVectorTable;
                    ReadOnlySequence<byte>[] sendBuffers = _aioSendBuffers;
                    Span<MemoryHandle> memoryHandles = MemoryHandles;
                    int memoryHandleCount = 0;
                    for (int i = 0; i < sendQueue.Count; i++)
                    {
                        TSocket socket = sendQueue[i].Socket;
                        ReadOnlySequence<byte> buffer;
                        Exception error = socket.GetReadResult(out buffer);
                        if (error != null)
                        {
                            if (error == TransportConstants.StopSentinel)
                            {
                                error = null;
                            }
                            socket.CompleteOutput(error);
                            completedCount++;
                        }
                        else
                        {
                            int ioVectorLength = socket.CalcIOVectorLengthForSend(ref buffer, IoVectorsPerAioSocket);
                            socket.FillSendIOVector(ref buffer, ioVectors, ioVectorLength, memoryHandles);
                            memoryHandles = memoryHandles.Slice(ioVectorLength);
                            memoryHandleCount += ioVectorLength;

                            aioCbs->aio_fildes = socket.Fd;
                            aioCbs->aio_data = (ulong)i;
                            aioCbs->aio_lio_opcode = IOCB_CMD_PWRITEV;
                            aioCbs->aio_buf = (ulong)ioVectors;
                            aioCbs->aio_nbytes = (ulong)ioVectorLength;
                            aioCbs++;

                            sendBuffers[sendCount] = buffer;
                            sendCount++;
                            if (sendCount == EventBufferLength)
                            {
                                break;
                            }

                            ioVectors += ioVectorLength;
                        }
                    }
                    if (sendCount > 0)
                    {
                        PosixResult res = AioInterop.IoSubmit(_aioContext, sendCount, AioCbsTable);

                        memoryHandles = MemoryHandles;
                        for (int i = 0; i < memoryHandleCount; i++)
                        {
                            memoryHandles[i].Dispose();
                        }

                        if (res != sendCount)
                        {
                            throw new NotSupportedException("Unexpected IoSubmit Send retval " + res);
                        }

                        io_event* aioEvents = AioEvents;
                        res = AioInterop.IoGetEvents(_aioContext, sendCount, aioEvents);
                        if (res != sendCount)
                        {
                            throw new NotSupportedException("Unexpected IoGetEvents Send retval " + res);
                        }

                        io_event* aioEvent = aioEvents;
                        for (int i = 0; i < sendCount; i++)
                        {
                            PosixResult result = new PosixResult((int)aioEvent->res);
                            int socketIndex = (int)aioEvent->data;
                            TSocket socket = sendQueue[socketIndex].Socket;
                            ReadOnlySequence<byte> buffer = sendBuffers[i]; // assumes in-order events
                            sendBuffers[i] = default;
                            socket.HandleSendResult(ref buffer, result, loop: false, zerocopy: false, zeroCopyRegistered: false);
                            aioEvent++;
                        }
                    }

                    sendQueue.RemoveRange(0, sendCount + completedCount);
                }
            }

            private unsafe void PerformSends(List<ScheduledSend> sendQueue, bool aioSend)
            {
                if (aioSend)
                {
                    AioSend(sendQueue);
                }
                else
                {
                    Span<MemoryHandle> receiveMemoryHandles = MemoryHandles;
                    for (int i = 0; i < sendQueue.Count; i++)
                    {
                        sendQueue[i].Socket.DoDeferedSend(receiveMemoryHandles);
                    }
                    sendQueue.Clear();
                }
            }

            private void CloseAccept()
            {
                bool lastSocket = false;
                var acceptSockets = _acceptSockets;
                lock (_sockets)
                {
                    for (int i = 0; i < acceptSockets.Count; i++)
                    {
                        lastSocket = RemoveSocket(acceptSockets[i].Fd);
                    }
                }
                for (int i = 0; i < acceptSockets.Count; i++)
                {
                    // close causes remove from epoll (CLOEXEC)
                    acceptSockets[i].Close(); // will close (no concurrent users)
                }
                acceptSockets.Clear();
                _acceptQueue.Writer.TryComplete();
                CompleteStateChange(TransportThreadState.AcceptClosed);
                if (lastSocket)
                {
                    StopThread();
                }
            }

            // must be called under tsocket.Gate
            public void UpdateEPollControl(TSocket tsocket, int flags, bool registered)
            {
                flags &= EPOLLIN | EPOLLOUT | EPOLLERR;
                EPollInterop.EPollControl(_epollFd,
                            registered ? EPOLL_CTL_MOD : EPOLL_CTL_ADD,
                            tsocket.Fd,
                            flags | EPOLLONESHOT,
                            tsocket.Fd);
            }

            internal static MemoryPool<byte> CreateMemoryPool()
            {
                return new SlabMemoryPool();
            }

            private struct ScheduledSend
            {
                public TSocket Socket;
            }
        }
    }
}