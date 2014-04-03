﻿using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using Mina.Core.Buffer;
using Mina.Core.Future;
using Mina.Core.Service;
using Mina.Core.Session;
using Mina.Core.Write;
using Mina.Util;

namespace Mina.Transport.Socket
{
    /// <summary>
    /// <see cref="IoAcceptor"/> for datagram transport (UDP/IP).
    /// </summary>
    public partial class AsyncDatagramAcceptor : AbstractIoAcceptor, IDatagramAcceptor, IoProcessor<AsyncDatagramSession>
    {
        private static readonly IoSessionRecycler DefaultRecycler = new ExpiringSessionRecycler();

        private IoSessionRecycler _sessionRecycler = DefaultRecycler;
        private readonly Dictionary<EndPoint, SocketContext> _listenSockets = new Dictionary<EndPoint, SocketContext>();

        public AsyncDatagramAcceptor()
            : base(new DefaultDatagramSessionConfig())
        {
            ReuseBuffer = true;
        }

        /// <inheritdoc/>
        public override ITransportMetadata TransportMetadata
        {
            get { return AsyncDatagramSession.Metadata; }
        }

        /// <summary>
        /// Gets or sets a value indicating whether to reuse the read buffer
        /// sent to <see cref="SocketSession.FilterChain"/> by
        /// <see cref="Core.Filterchain.IoFilterChain.FireMessageReceived(Object)"/>.
        /// </summary>
        /// <remarks>
        /// If any thread model, i.e. an <see cref="Filter.Executor.ExecutorFilter"/>,
        /// is added before filters that process the incoming <see cref="Core.Buffer.IoBuffer"/>
        /// in <see cref="Core.Filterchain.IoFilter.MessageReceived(Core.Filterchain.INextFilter, IoSession, Object)"/>,
        /// this must be set to <code>false</code> to avoid undetermined state
        /// of the read buffer. The default value is <code>true</code>.
        /// </remarks>
        public Boolean ReuseBuffer { get; set; }

        /// <inheritdoc/>
        public IoSessionRecycler SessionRecycler
        {
            get { return _sessionRecycler; }
            set { _sessionRecycler = value; }
        }

        /// <inheritdoc/>
        protected override IEnumerable<EndPoint> BindInternal(IEnumerable<EndPoint> localEndPoints)
        {
            Dictionary<EndPoint, System.Net.Sockets.Socket> newListeners = new Dictionary<EndPoint, System.Net.Sockets.Socket>();
            try
            {
                // Process all the addresses
                foreach (EndPoint localEP in localEndPoints)
                {
                    EndPoint ep = localEP;
                    if (ep == null)
                        ep = new IPEndPoint(IPAddress.Any, 0);
                    System.Net.Sockets.Socket listenSocket = new System.Net.Sockets.Socket(ep.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                    listenSocket.Bind(ep);
                    newListeners[listenSocket.LocalEndPoint] = listenSocket;
                }
            }
            catch (Exception)
            {
                // Roll back if failed to bind all addresses
                foreach (System.Net.Sockets.Socket listenSocket in newListeners.Values)
                {
                    try
                    {
                        listenSocket.Close();
                    }
                    catch (Exception ex)
                    {
                        ExceptionMonitor.Instance.ExceptionCaught(ex);
                    }
                }

                throw;
            }

            foreach (KeyValuePair<EndPoint, System.Net.Sockets.Socket> pair in newListeners)
            {
                SocketContext ctx = new SocketContext(pair.Value, SessionConfig);
                _listenSockets[pair.Key] = ctx;
                BeginReceive(ctx);
            }

            return newListeners.Keys;
        }

        /// <inheritdoc/>
        protected override void UnbindInternal(IEnumerable<EndPoint> localEndPoints)
        {
            foreach (EndPoint ep in localEndPoints)
            {
                SocketContext ctx;
                if (!_listenSockets.TryGetValue(ep, out ctx))
                    continue;
                _listenSockets.Remove(ep);
                ctx.Close();
            }
        }

        private void EndReceive(SocketContext ctx, IoBuffer buf, EndPoint remoteEP)
        {
            IoSession session = NewSessionWithoutLock(remoteEP, ctx);
            session.FilterChain.FireMessageReceived(buf);
            BeginReceive(ctx);
        }

        private IoSession NewSessionWithoutLock(EndPoint remoteEP, SocketContext ctx)
        {
            IoSession session;
            lock (_sessionRecycler)
            {
                session = _sessionRecycler.Recycle(remoteEP);

                if (session != null)
                    return session;

                // If a new session needs to be created.
                session = new AsyncDatagramSession(this, this, ctx, remoteEP, ReuseBuffer);
                _sessionRecycler.Put(session);
            }

            InitSession<IoFuture>(session, null, null);

            try
            {
                FilterChainBuilder.BuildFilterChain(session.FilterChain);

                IoServiceSupport serviceSupport = session.Service as IoServiceSupport;
                if (serviceSupport != null)
                    serviceSupport.FireSessionCreated(session);
            }
            catch (Exception ex)
            {
                ExceptionMonitor.Instance.ExceptionCaught(ex);
            }

            return session;
        }

        internal partial class SocketContext
        {
            public readonly System.Net.Sockets.Socket _socket;
            private readonly ConcurrentQueue<AsyncDatagramSession> _flushingSessions = new ConcurrentQueue<AsyncDatagramSession>();
            private Int32 _writing;
            private AsyncDatagramSession _currentWriteSession;

            public System.Net.Sockets.Socket Socket { get { return _socket; } }

            public void Flush(AsyncDatagramSession session)
            {
                if (ScheduleFlush(session))
                    Flush();
            }

            private Boolean ScheduleFlush(AsyncDatagramSession session)
            {
                if (session.ScheduledForFlush())
                {
                    _flushingSessions.Enqueue(session);
                    return true;
                }
                else
                {
                    return false;
                }
            }

            private void Flush()
            {
                if (Interlocked.CompareExchange(ref _writing, 1, 0) > 0)
                    return;
                BeginSend();
            }

            private void BeginSend()
            {
                AsyncDatagramSession session = _currentWriteSession;
                if (session == null)
                {
                    if (!_flushingSessions.TryDequeue(out session))
                    {
                        Interlocked.Exchange(ref _writing, 0);
                        return;
                    }

                    _currentWriteSession = session;
                }

                BeginSend(session);
            }

            private void BeginSend(AsyncDatagramSession session)
            {
                IWriteRequest req = session.CurrentWriteRequest;
                if (req == null)
                {
                    req = session.WriteRequestQueue.Poll(session);

                    if (req == null)
                    {
                        // TODO what?
                        return;
                    }

                    session.CurrentWriteRequest = req;
                }

                IoBuffer buf = req.Message as IoBuffer;
                if (buf == null)
                    EndSend(session, new InvalidOperationException("Don't know how to handle message of type '"
                            + req.Message.GetType().Name + "'.  Are you missing a protocol encoder?"));

                if (buf.HasRemaining)
                {
                    EndPoint destination = req.Destination;
                    if (destination == null)
                        destination = session.RemoteEndPoint;
                    BeginSend(session, buf, destination);
                }
                else
                {
                    EndSend(session, 0);
                }
            }

            private void EndSend(AsyncDatagramSession session, Int32 bytesTransferred)
            {
                session.IncreaseWrittenBytes(bytesTransferred, DateTime.Now);

                IWriteRequest req = session.CurrentWriteRequest;
                if (req != null)
                {
                    IoBuffer buf = req.Message as IoBuffer;
                    if (buf == null)
                    {
                        // we only send buffers and files so technically it shouldn't happen
                    }
                    else
                    {
                        // Buffer has been sent, clear the current request.
                        Int32 pos = buf.Position;
                        buf.Reset();

                        session.CurrentWriteRequest = null;
                        try
                        {
                            session.FilterChain.FireMessageSent(req);
                        }
                        catch (Exception ex)
                        {
                            session.FilterChain.FireExceptionCaught(ex);
                        }

                        // And set it back to its position
                        buf.Position = pos;
                    }
                }

                BeginSend();
            }

            private void EndSend(AsyncDatagramSession session, Exception ex)
            {
                IWriteRequest req = session.CurrentWriteRequest;
                if (req != null)
                    req.Future.Exception = ex;
                session.FilterChain.FireExceptionCaught(ex);
                BeginSend();
            }
        }

        #region IoProcessor

        public void Add(AsyncDatagramSession session)
        {
            // do nothing for UDP
        }

        public void Write(AsyncDatagramSession session, IWriteRequest writeRequest)
        {
            session.WriteRequestQueue.Offer(session, writeRequest);
            Flush(session);
        }

        public void Flush(AsyncDatagramSession session)
        {
            session.Context.Flush(session);
        }

        public void Remove(AsyncDatagramSession session)
        {
            SessionRecycler.Remove(session);
            IoServiceSupport support = session.Service as IoServiceSupport;
            if (support != null)
                support.FireSessionDestroyed(session);
        }

        public void UpdateTrafficControl(AsyncDatagramSession session)
        {
            throw new NotSupportedException();
        }

        void IoProcessor.Write(IoSession session, IWriteRequest writeRequest)
        {
            Write((AsyncDatagramSession)session, writeRequest);
        }

        void IoProcessor.Flush(IoSession session)
        {
            Flush((AsyncDatagramSession)session);
        }

        void IoProcessor.Add(IoSession session)
        {
            Add((AsyncDatagramSession)session);
        }

        void IoProcessor.Remove(IoSession session)
        {
            Remove((AsyncDatagramSession)session);
        }

        void IoProcessor.UpdateTrafficControl(IoSession session)
        {
            UpdateTrafficControl((AsyncDatagramSession)session);
        }

        #endregion
    }
}